using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Emby.Naming.ExternalFiles;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.API.Models;
using Shokofin.Configuration;
using Shokofin.ExternalIds;
using Shokofin.Utils;

using File = System.IO.File;
using TvSeries = MediaBrowser.Controller.Entities.TV.Series;

namespace Shokofin.Resolvers;

public class ShokoResolveManager
{
    private readonly ShokoAPIManager ApiManager;

    private readonly ShokoAPIClient ApiClient;

    private readonly IIdLookup Lookup;

    private readonly ILibraryManager LibraryManager;

    private readonly IFileSystem FileSystem;

    private readonly ILogger<ShokoResolveManager> Logger;

    private readonly NamingOptions NamingOptions;

    private readonly ExternalPathParser ExternalPathParser;

    private readonly GuardedMemoryCache DataCache;

    // Note: Out of the 14k entries in my test shoko database, then only **319** entries have a title longer than 100 characters.
    private const int NameCutOff = 64;

    private static readonly HashSet<string> IgnoreFolderNames = [
        "backdrops",
        "behind the scenes",
        "deleted scenes",
        "interviews",
        "scenes",
        "samples",
        "shorts",
        "featurettes",
        "clips",
        "other",
        "extras",
        "trailers",
    ];

    public bool IsCacheStalled => DataCache.IsStalled;

    public event EventHandler<MediaConfigurationChangedEventArgs>? AddedConfiguration;

    public event EventHandler<MediaConfigurationChangedEventArgs>? RemovedConfiguration;

    public ShokoResolveManager(
        ShokoAPIManager apiManager,
        ShokoAPIClient apiClient,
        IIdLookup lookup,
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<ShokoResolveManager> logger,
        ILocalizationManager localizationManager,
        NamingOptions namingOptions
    )
    {
        ApiManager = apiManager;
        ApiClient = apiClient;
        Lookup = lookup;
        LibraryManager = libraryManager;
        FileSystem = fileSystem;
        Logger = logger;
        DataCache = new(logger, TimeSpan.FromMinutes(15), new() { ExpirationScanFrequency = TimeSpan.FromMinutes(25) }, new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1), SlidingExpiration = TimeSpan.FromMinutes(15) });
        NamingOptions = namingOptions;
        ExternalPathParser = new ExternalPathParser(namingOptions, localizationManager, MediaBrowser.Model.Dlna.DlnaProfileType.Subtitle);
        LibraryManager.ItemRemoved += OnLibraryManagerItemRemoved;
    }

    ~ShokoResolveManager()
    {
        LibraryManager.ItemRemoved -= OnLibraryManagerItemRemoved;
        DataCache.Dispose();
    }

    public void Clear()
    {
        Logger.LogDebug("Clearing data…");
        DataCache.Clear();
    }

    #region Changes Tracking

    private void OnLibraryManagerItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        // Remove the VFS directory for any media library folders when they're removed.
        var root = LibraryManager.RootFolder;
        if (e.Item != null && root != null && e.Item != root && e.Item is Folder folder && folder.ParentId == Guid.Empty  && !string.IsNullOrEmpty(folder.Path) && !folder.Path.StartsWith(root.Path)) {
            DataCache.Remove($"paths-for-media-folder:{folder.Path}");
            DataCache.Remove($"should-skip-media-folder:{folder.Path}");
            var mediaFolderConfig = Plugin.Instance.Configuration.MediaFolders.FirstOrDefault(c => c.MediaFolderId == folder.Id);
            if (mediaFolderConfig != null) {
                Logger.LogDebug(
                    "Removing stored configuration for folder at {Path} (ImportFolder={ImportFolderId},RelativePath={RelativePath})",
                    folder.Path,
                    mediaFolderConfig.ImportFolderId,
                    mediaFolderConfig.ImportFolderRelativePath
                );
                Plugin.Instance.Configuration.MediaFolders.Remove(mediaFolderConfig);
                Plugin.Instance.SaveConfiguration();

                RemovedConfiguration?.Invoke(null, new(mediaFolderConfig, folder));
            }
            var vfsPath = ShokoAPIManager.GetVirtualRootForMediaFolder(folder);
            if (Directory.Exists(vfsPath)) {
                Logger.LogInformation("Removing VFS directory for folder at {Path}", folder.Path);
                Directory.Delete(vfsPath, true);
                Logger.LogInformation("Removed VFS directory for folder at {Path}", folder.Path);
            }
        }
    }

    #endregion

    #region Media Folder Mapping

    private IReadOnlySet<string> GetPathsForMediaFolder(Folder mediaFolder)
        => DataCache.GetOrCreate<IReadOnlySet<string>>(
            $"paths-for-media-folder:{mediaFolder.Path}",
            (paths) => Logger.LogTrace("Reusing {FileCount} files for folder at {Path}", paths.Count, mediaFolder.Path),
            (_) => {
                Logger.LogDebug("Looking for files in folder at {Path}", mediaFolder.Path);
                var start = DateTime.UtcNow;
                var paths = FileSystem.GetFilePaths(mediaFolder.Path, true)
                    .Where(path => NamingOptions.VideoFileExtensions.Contains(Path.GetExtension(path)))
                    .ToHashSet();
                Logger.LogDebug("Found {FileCount} files in folder at {Path} in {TimeSpan}.", paths.Count, mediaFolder.Path, DateTime.UtcNow - start);
                return paths;
            },
            new() {
                SlidingExpiration = TimeSpan.FromMinutes(30),
            }
        );

    public IReadOnlyList<(MediaFolderConfiguration config, Folder mediaFolder, string vfsPath)> GetAvailableMediaFolders(bool fileEvents = false, bool refreshEvents = false)
        => Plugin.Instance.Configuration.MediaFolders
            .Where(mediaFolder => mediaFolder.IsMapped && (!fileEvents || mediaFolder.IsFileEventsEnabled) && (!refreshEvents || mediaFolder.IsRefreshEventsEnabled))
            .Select(config => (config,  mediaFolder: LibraryManager.GetItemById(config.MediaFolderId) as Folder))
            .OfType<(MediaFolderConfiguration config, Folder mediaFolder)>()
            .Select(tuple => (tuple.config, tuple.mediaFolder, ShokoAPIManager.GetVirtualRootForMediaFolder(tuple.mediaFolder)))
            .ToList();

    public async Task<MediaFolderConfiguration> GetOrCreateConfigurationForMediaFolder(Folder mediaFolder)
    {
        var config = Plugin.Instance.Configuration;
        var mediaFolderConfig = config.MediaFolders.FirstOrDefault(c => c.MediaFolderId == mediaFolder.Id);
        if (mediaFolderConfig != null)
            return mediaFolderConfig;

        // Check if we should introduce the VFS for the media folder.
        mediaFolderConfig = new() {
            MediaFolderId = mediaFolder.Id,
            MediaFolderPath = mediaFolder.Path,
            IsFileEventsEnabled = config.SignalR_FileEvents,
            IsRefreshEventsEnabled = config.SignalR_RefreshEnabled,
            IsVirtualFileSystemEnabled = config.VirtualFileSystem,
            LibraryFilteringMode = config.LibraryFilteringMode,
        };

        var start = DateTime.UtcNow;
        var attempts = 0;
        var samplePaths = FileSystem.GetFilePaths(mediaFolder.Path, true)
            .Where(path => NamingOptions.VideoFileExtensions.Contains(Path.GetExtension(path)))
            .Take(100)
            .ToList();

        Logger.LogDebug("Asking remote server if it knows any of the {Count} sampled files in {Path}.", samplePaths.Count > 100 ? 100 : samplePaths.Count, mediaFolder.Path);
        foreach (var path in samplePaths) {
            attempts++;
            var partialPath = path[mediaFolder.Path.Length..];
            var files = await ApiClient.GetFileByPath(partialPath).ConfigureAwait(false);
            var file = files.FirstOrDefault();
            if (file == null)
                continue;

            var fileId = file.Id.ToString();
            var fileLocations = file.Locations
                .Where(location => location.RelativePath.EndsWith(partialPath))
                .ToList();
            if (fileLocations.Count == 0)
                continue;

            var fileLocation = fileLocations[0];
            mediaFolderConfig.ImportFolderId = fileLocation.ImportFolderId;
            mediaFolderConfig.ImportFolderRelativePath = fileLocation.RelativePath[..^partialPath.Length];
            break;
        }

        try {
            var importFolder = await ApiClient.GetImportFolder(mediaFolderConfig.ImportFolderId);
            if (importFolder != null)
                mediaFolderConfig.ImportFolderName = importFolder.Name;
        }
        catch { }

        // Store and log the result.
        config.MediaFolders.Add(mediaFolderConfig);
        Plugin.Instance.SaveConfiguration(config);
        if (mediaFolderConfig.IsMapped) {
            Logger.LogInformation(
                "Found a match for media folder at {Path} in {TimeSpan} (ImportFolder={FolderId},RelativePath={RelativePath},MediaLibrary={Path},Attempts={Attempts})",
                mediaFolder.Path,
                DateTime.UtcNow - start,
                mediaFolderConfig.ImportFolderId,
                mediaFolderConfig.ImportFolderRelativePath,
                mediaFolder.Path,
                attempts
            );
        }
        else {
            Logger.LogWarning(
                "Failed to find a match for media folder at {Path} after {Amount} attempts in {TimeSpan}.",
                mediaFolder.Path,
                attempts,
                DateTime.UtcNow - start
            );
        }

        AddedConfiguration?.Invoke(null, new(mediaFolderConfig, mediaFolder));

        return mediaFolderConfig;
    }

    #endregion

    #region Generate Structure

    /// <summary>
    /// Generates the VFS structure if the VFS is enabled globally or on the
    /// <paramref name="mediaFolder"/>.
    /// </summary>
    /// <param name="mediaFolder">The media folder to generate a structure for.</param>
    /// <param name="path">The file or folder within the media folder to generate a structure for.</param>
    /// <returns>The VFS path, if it succeeded.</returns>
    private async Task<string?> GenerateStructureInVFS(Folder mediaFolder, string path)
    {
        // Skip link generation if we've already generated for the media folder.
        var vfsPath = ShokoAPIManager.GetVirtualRootForMediaFolder(mediaFolder);
        if (DataCache.TryGetValue<bool>($"should-skip-media-folder:{mediaFolder.Path}", out var shouldReturnPath))
            return shouldReturnPath ? vfsPath : null;

        // Check full path and all parent directories if they have been indexed.
        if (path.StartsWith(vfsPath + Path.DirectorySeparatorChar)) {
            var pathSegments = path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar).Prepend(vfsPath).ToArray();
            while (pathSegments.Length > 1) {
                var subPath = Path.Join(pathSegments);
                if (DataCache.TryGetValue<bool>($"should-skip-vfs-path:{subPath}", out _))
                    return vfsPath;
                pathSegments = pathSegments.SkipLast(1).ToArray();
            }
        }

        // Only do this once.
        var key = path.StartsWith(mediaFolder.Path)
            ? $"should-skip-media-folder:{mediaFolder.Path}"
            : $"should-skip-vfs-path:{path}";
        shouldReturnPath = await DataCache.GetOrCreateAsync<bool>(key, async (__) => {
            var mediaConfig = await GetOrCreateConfigurationForMediaFolder(mediaFolder);
            if (!mediaConfig.IsMapped)
                return false;

            // Return early if we're not going to generate them.
            if (!mediaConfig.IsVirtualFileSystemEnabled)
                return false;

            if (!Plugin.Instance.CanCreateSymbolicLinks)
                throw new Exception("Windows users are required to enable Developer Mode then restart Jellyfin to be able to create symbolic links, a feature required to use the VFS.");

            // Iterate the files already in the VFS.
            string? pathToClean = null;
            IEnumerable<(string sourceLocation, string fileId, string seriesId)>? allFiles = null;
            if (path.StartsWith(vfsPath + Path.DirectorySeparatorChar)) {
                var allPaths = GetPathsForMediaFolder(mediaFolder);
                var pathSegments = path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar);
                switch (pathSegments.Length) {
                    // show/movie-folder level
                    case 1: {
                        var seriesName = pathSegments[0];
                        if (!seriesName.TryGetAttributeValue(ShokoSeriesId.Name, out var seriesId) || !int.TryParse(seriesId, out _))
                            break;

                        // movie-folder
                        if (seriesName.TryGetAttributeValue(ShokoEpisodeId.Name, out var episodeId) ) {
                            if (!int.TryParse(episodeId, out _))
                                break;

                            pathToClean = path;
                            allFiles = GetFilesForMovie(episodeId, seriesId, mediaConfig.ImportFolderId, mediaConfig.ImportFolderRelativePath, mediaFolder.Path, allPaths);
                            break;
                        }

                        // show
                        pathToClean = path;
                        allFiles = GetFilesForShow(seriesId, null, mediaConfig.ImportFolderId, mediaConfig.ImportFolderRelativePath, mediaFolder.Path, allPaths);
                        break;
                    }

                    // season/movie level
                    case 2: {
                        var (seriesName, seasonOrMovieName) = pathSegments;
                        if (!seriesName.TryGetAttributeValue(ShokoSeriesId.Name, out var seriesId) || !int.TryParse(seriesId, out _))
                            break;

                        // movie
                        if (seriesName.TryGetAttributeValue(ShokoEpisodeId.Name, out _)) {
                            if (!seasonOrMovieName.TryGetAttributeValue(ShokoSeriesId.Name, out seriesId) || !int.TryParse(seriesId, out _))
                                break;

                            if (!seasonOrMovieName.TryGetAttributeValue(ShokoFileId.Name, out var fileId) || !int.TryParse(fileId, out _))
                                break;

                            allFiles = GetFilesForEpisode(fileId, seriesId, mediaConfig.ImportFolderId, mediaConfig.ImportFolderRelativePath, mediaFolder.Path);
                            break;
                        }

                        // "season" or extras
                        if (!seasonOrMovieName.StartsWith("Season ") || !int.TryParse(seasonOrMovieName.Split(' ').Last(), out var seasonNumber))
                            break;

                        pathToClean = path;
                        allFiles = GetFilesForShow(seriesId, seasonNumber, mediaConfig.ImportFolderId, mediaConfig.ImportFolderRelativePath, mediaFolder.Path, allPaths);
                        break;
                    }

                    // episodes level
                    case 3: {
                        var (seriesName, seasonName, episodeName) = pathSegments;
                        if (!seriesName.TryGetAttributeValue(ShokoSeriesId.Name, out var seriesId) || !int.TryParse(seriesId, out _))
                            break;

                        if (!seasonName.StartsWith("Season ") || !int.TryParse(seasonName.Split(' ').Last(), out _))
                            break;

                        if (!episodeName.TryGetAttributeValue(ShokoSeriesId.Name, out seriesId) || !int.TryParse(seriesId, out _))
                            break;

                        if (!episodeName.TryGetAttributeValue(ShokoFileId.Name, out var fileId) || !int.TryParse(fileId, out _))
                            break;

                        allFiles = GetFilesForEpisode(fileId, seriesId, mediaConfig.ImportFolderId, mediaConfig.ImportFolderRelativePath, mediaFolder.Path);
                        break;
                    }
                }
            }
            // Iterate files in the "real" media folder.
            else if (path.StartsWith(mediaFolder.Path)) {
                var allPaths = GetPathsForMediaFolder(mediaFolder);
                pathToClean = vfsPath;
                allFiles = GetFilesForImportFolder(mediaConfig.ImportFolderId, mediaConfig.ImportFolderRelativePath, mediaFolder.Path, allPaths);
            }

            if (allFiles == null)
                return false;

            // Generate and cleanup the structure in the VFS.
            var result = await GenerateStructure(mediaFolder, vfsPath, allFiles);
            if (!string.IsNullOrEmpty(pathToClean))
                result += CleanupStructure(vfsPath, pathToClean, result.Paths.ToArray());

            // Save which paths we've already generated so we can skip generation
            // for them and their sub-paths later, and also print the result.
            result.Print(Logger, path.StartsWith(mediaFolder.Path) ? mediaFolder.Path : path);

            return true;
        });

        return shouldReturnPath ? vfsPath : null;
    }

    public IEnumerable<(string sourceLocation, string fileId, string seriesId)> GetFilesForEpisode(string fileId, string seriesId, int importFolderId, string importFolderSubPath, string mediaFolderPath)
    {
        var start = DateTime.UtcNow;
        var file = ApiClient.GetFile(fileId).ConfigureAwait(false).GetAwaiter().GetResult();
        if (file == null || !file.CrossReferences.Any(xref => xref.Series.ToString() == seriesId))
            yield break;
        Logger.LogDebug(
            "Iterating 1 file to potentially use within media folder at {Path} (File={FileId},Series={SeriesId},ImportFolder={FolderId},RelativePath={RelativePath})",
            mediaFolderPath,
            fileId,
            seriesId,
            importFolderId,
            importFolderSubPath
        );

        var location = file.Locations
            .Where(location => location.ImportFolderId == importFolderId && (importFolderSubPath.Length == 0 || location.RelativePath.StartsWith(importFolderSubPath)))
            .FirstOrDefault();
        if (location == null || file.CrossReferences.Count == 0)
            yield break;

        var sourceLocation = Path.Join(mediaFolderPath, location.RelativePath[importFolderSubPath.Length..]);
        if (!File.Exists(sourceLocation))
            yield break;

        yield return (sourceLocation, fileId, seriesId);

        var timeSpent = DateTime.UtcNow - start;
        Logger.LogDebug(
            "Iterated 1 file to potentially use within media folder at {Path} in {TimeSpan} (File={FileId},Series={SeriesId},ImportFolder={FolderId},RelativePath={RelativePath})",
            mediaFolderPath,
            timeSpent,
            fileId,
            seriesId,
            importFolderId,
            importFolderSubPath
        );
    }

    public IEnumerable<(string sourceLocation, string fileId, string seriesId)> GetFilesForMovie(string episodeId, string seriesId, int importFolderId, string importFolderSubPath, string mediaFolderPath, IReadOnlySet<string> fileSet)
    {
        var start = DateTime.UtcNow;
        var totalFiles = 0;
        var seasonInfo = ApiManager.GetSeasonInfoForSeries(seriesId).ConfigureAwait(false).GetAwaiter().GetResult();
        if (seasonInfo == null)
            yield break;
        Logger.LogDebug(
            "Iterating files to potentially use within media folder at {Path} (Episode={EpisodeId},Series={SeriesId},ImportFolder={FolderId},RelativePath={RelativePath})",
            mediaFolderPath,
            episodeId,
            seriesId,
            importFolderId,
            importFolderSubPath
        );

        var episodeIds = seasonInfo.EpisodeList.Concat(seasonInfo.AlternateEpisodesList).Concat(seasonInfo.ExtrasList).Select(episode => episode.Id).Append(episodeId).ToHashSet();
        var files = ApiClient.GetFilesForSeries(seasonInfo.Id).ConfigureAwait(false).GetAwaiter().GetResult();
        var fileLocations = files
            .Where(files => files.CrossReferences.Any(xref => episodeIds.Overlaps(xref.Episodes.Where(e => e.Shoko.HasValue).Select(e => e.Shoko!.Value.ToString()))))
            .SelectMany(file => file.Locations.Select(location => (file, location)))
            .ToList();
        foreach (var (file, location) in fileLocations) {
            if (location.ImportFolderId != importFolderId || importFolderSubPath.Length != 0 && !location.RelativePath.StartsWith(importFolderSubPath))
                continue;

            var sourceLocation = Path.Join(mediaFolderPath, location.RelativePath[importFolderSubPath.Length..]);
            if (!fileSet.Contains(sourceLocation))
                continue;

            totalFiles++;
            yield return (sourceLocation, file.Id.ToString(), seasonInfo.Id);
        }
        var timeSpent = DateTime.UtcNow - start;
        Logger.LogDebug(
            "Iterated {FileCount} file to potentially use within media folder at {Path} in {TimeSpan} (Episode={EpisodeId},Series={SeriesId},ImportFolder={FolderId},RelativePath={RelativePath})",
            totalFiles,
            mediaFolderPath,
            timeSpent,
            episodeId,
            seriesId,
            importFolderId,
            importFolderSubPath
        );
    }

    private IEnumerable<(string sourceLocation, string fileId, string seriesId)> GetFilesForShow(string seriesId, int? seasonNumber, int importFolderId, string importFolderSubPath, string mediaFolderPath, IReadOnlySet<string> fileSet)
    {
        var start = DateTime.UtcNow;
        var showInfo = ApiManager.GetShowInfoForSeries(seriesId).ConfigureAwait(false).GetAwaiter().GetResult();
        if (showInfo == null)
            yield break;
        Logger.LogDebug(
            "Iterating files to potentially use within media folder at {Path} (Series={SeriesId},Season={SeasonNumber},ImportFolder={FolderId},RelativePath={RelativePath})",
            mediaFolderPath,
            seriesId,
            seasonNumber,
            importFolderId,
            importFolderSubPath
        );

        // Only return the files for the given season.
        var totalFiles = 0;
        if (seasonNumber.HasValue) {
            // Special handling of specials (pun intended)
            if (seasonNumber.Value == 0) {
                foreach (var seasonInfo in showInfo.SeasonList) {
                    var episodeIds = seasonInfo.SpecialsList.Select(episode => episode.Id).ToHashSet();
                    var files = ApiClient.GetFilesForSeries(seasonInfo.Id).ConfigureAwait(false).GetAwaiter().GetResult();
                    var fileLocations = files
                        .Where(files => files.CrossReferences.Any(xref => episodeIds.Overlaps(xref.Episodes.Where(e => e.Shoko.HasValue).Select(e => e.Shoko!.Value.ToString()))))
                        .SelectMany(file => file.Locations.Select(location => (file, location)))
                        .ToList();
                    foreach (var (file, location) in fileLocations) {
                        if (location.ImportFolderId != importFolderId || importFolderSubPath.Length != 0 && !location.RelativePath.StartsWith(importFolderSubPath))
                            continue;

                        var sourceLocation = Path.Join(mediaFolderPath, location.RelativePath[importFolderSubPath.Length..]);
                        if (!fileSet.Contains(sourceLocation))
                            continue;

                        totalFiles++;
                        yield return (sourceLocation, file.Id.ToString(), seasonInfo.Id);
                    }
                }
            }
            // All other seasons.
            else {
                var seasonInfo = showInfo.GetSeasonInfoBySeasonNumber(seasonNumber.Value);
                if (seasonInfo != null) {
                    var baseNumber = showInfo.GetBaseSeasonNumberForSeasonInfo(seasonInfo);
                    var offset = seasonNumber.Value - baseNumber;
                    var episodeIds = (offset == 0 ? seasonInfo.EpisodeList.Concat(seasonInfo.ExtrasList) : seasonInfo.AlternateEpisodesList).Select(episode => episode.Id).ToHashSet();
                    var files = ApiClient.GetFilesForSeries(seasonInfo.Id).ConfigureAwait(false).GetAwaiter().GetResult();
                    var fileLocations = files
                        .Where(files => files.CrossReferences.Any(xref => episodeIds.Overlaps(xref.Episodes.Where(e => e.Shoko.HasValue).Select(e => e.Shoko!.Value.ToString()))))
                        .SelectMany(file => file.Locations.Select(location => (file, location)))
                        .ToList();
                    foreach (var (file, location) in fileLocations) {
                        if (location.ImportFolderId != importFolderId || importFolderSubPath.Length != 0 && !location.RelativePath.StartsWith(importFolderSubPath))
                            continue;

                        var sourceLocation = Path.Join(mediaFolderPath, location.RelativePath[importFolderSubPath.Length..]);
                        if (!fileSet.Contains(sourceLocation))
                            continue;

                        totalFiles++;
                        yield return (sourceLocation, file.Id.ToString(), seasonInfo.Id);
                    }
                }
            }
        }
        // Return all files for the show.
        else {
            foreach (var seasonInfo in showInfo.SeasonList) {
                var files = ApiClient.GetFilesForSeries(seasonInfo.Id).ConfigureAwait(false).GetAwaiter().GetResult();
                var fileLocations = files
                    .SelectMany(file => file.Locations.Select(location => (file, location)))
                    .ToList();
                foreach (var (file, location) in fileLocations) {
                    if (location.ImportFolderId != importFolderId || importFolderSubPath.Length != 0 && !location.RelativePath.StartsWith(importFolderSubPath))
                        continue;

                    var sourceLocation = Path.Join(mediaFolderPath, location.RelativePath[importFolderSubPath.Length..]);
                    if (!fileSet.Contains(sourceLocation))
                        continue;

                    totalFiles++;
                    yield return (sourceLocation, file.Id.ToString(), seasonInfo.Id);
                }
            }
        }

        var timeSpent = DateTime.UtcNow - start;
        Logger.LogDebug(
            "Iterated {FileCount} files to potentially use within media folder at {Path} in {TimeSpan} (Series={SeriesId},Season={SeasonNumber},ImportFolder={FolderId},RelativePath={RelativePath})",
            totalFiles,
            mediaFolderPath,
            timeSpent,
            seriesId,
            seasonNumber,
            importFolderId,
            importFolderSubPath
        );
    }

    private IEnumerable<(string sourceLocation, string fileId, string seriesId)> GetFilesForImportFolder(int importFolderId, string importFolderSubPath, string mediaFolderPath, IReadOnlySet<string> fileSet)
    {
        var start = DateTime.UtcNow;
        var firstPage = ApiClient.GetFilesForImportFolder(importFolderId, importFolderSubPath);
        var pageData = firstPage
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
        var totalPages = pageData.List.Count == pageData.Total ? 1 : (int)Math.Ceiling((float)pageData.Total / pageData.List.Count);
        Logger.LogDebug(
            "Iterating ≤{FileCount} files to potentially use within media folder at {Path} by checking {TotalCount} matches. (ImportFolder={FolderId},RelativePath={RelativePath},PageSize={PageSize},TotalPages={TotalPages})",
            fileSet.Count,
            mediaFolderPath,
            pageData.Total,
            importFolderId,
            importFolderSubPath,
            pageData.List.Count == pageData.Total ? null : pageData.List.Count,
            totalPages
        );

        // Ensure at most 5 pages are in-flight at any given time, until we're done fetching the pages.
        var semaphore = new SemaphoreSlim(5);
        var pages = new List<Task<ListResult<API.Models.File>>>() { firstPage };
        for (var page = 2; page <= totalPages; page++)
            pages.Add(GetImportFolderFilesPage(importFolderId, importFolderSubPath, page, semaphore));

        var singleSeriesIds = new HashSet<int>();
        var multiSeriesFiles = new List<(API.Models.File, string)>();
        var totalSingleSeriesFiles = 0;
        do {
            var task = Task.WhenAny(pages).ConfigureAwait(false).GetAwaiter().GetResult();
            pages.Remove(task);
            semaphore.Release();
            pageData = task.Result;

            Logger.LogTrace(
                "Iterating page {PageNumber} with size {PageSize} (ImportFolder={FolderId},RelativePath={RelativePath})",
                totalPages - pages.Count,
                pageData.List.Count,
                importFolderId,
                importFolderSubPath
            );
            foreach (var file in pageData.List) {
                if (file.CrossReferences.Count == 0)
                    continue;

                var location = file.Locations
                    .Where(location => location.ImportFolderId == importFolderId && (importFolderSubPath.Length == 0 || location.RelativePath.StartsWith(importFolderSubPath)))
                    .FirstOrDefault();
                if (location == null)
                    continue;

                var sourceLocation = Path.Join(mediaFolderPath, location.RelativePath[importFolderSubPath.Length..]);
                if (!fileSet.Contains(sourceLocation))
                    continue;

                // Yield all single-series files now, and offset the processing of all multi-series files for later.
                var seriesIds = file.CrossReferences.Where(x => x.Series.Shoko.HasValue && x.Episodes.All(e => e.Shoko.HasValue)).Select(x => x.Series.Shoko!.Value).ToHashSet();
                if (seriesIds.Count == 1) {
                    totalSingleSeriesFiles++;
                    singleSeriesIds.Add(seriesIds.First());
                    foreach (var seriesId in seriesIds)
                        yield return (sourceLocation, file.Id.ToString(), seriesId.ToString());
                }
                else if (seriesIds.Count > 1) {
                    multiSeriesFiles.Add((file, sourceLocation));
                }
            }
        } while (pages.Count > 0);

        // Check which series of the multiple series we have, and only yield
        // the paths for the series we have. This will fail if an OVA episode is
        // linked to both the OVA and e.g. a specials for the TV Series.
        var totalMultiSeriesFiles = 0;
        foreach (var (file, sourceLocation) in multiSeriesFiles) {
            var crossReferences = file.CrossReferences
                .Where(xref => xref.Series.Shoko.HasValue && xref.Episodes.All(e => e.Shoko.HasValue) && singleSeriesIds.Contains(xref.Series.Shoko!.Value))
                .Select(xref => xref.Series.Shoko!.Value.ToString())
                .ToHashSet();
            foreach (var seriesId in crossReferences)
                yield return (sourceLocation, file.Id.ToString(), seriesId);
            totalMultiSeriesFiles += crossReferences.Count;
        }

        var timeSpent = DateTime.UtcNow - start;
        Logger.LogDebug(
            "Iterated {FileCount} ({MultiFileCount}→{MultiFileCount}) files to potentially use within media folder at {Path} in {TimeSpan} (ImportFolder={FolderId},RelativePath={RelativePath})",
            totalSingleSeriesFiles,
            multiSeriesFiles.Count,
            totalMultiSeriesFiles,
            mediaFolderPath,
            timeSpent,
            importFolderId,
            importFolderSubPath
        );
    }

    private async Task<ListResult<API.Models.File>> GetImportFolderFilesPage(int importFolderId, string importFolderSubPath, int page, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync().ConfigureAwait(false);
        return await ApiClient.GetFilesForImportFolder(importFolderId, importFolderSubPath, page).ConfigureAwait(false);
    }

    private async Task<LinkGenerationResult> GenerateStructure(Folder mediaFolder, string vfsPath, IEnumerable<(string sourceLocation, string fileId, string seriesId)> allFiles)
    {
        var result = new LinkGenerationResult();
        var collectionType = LibraryManager.GetInheritedContentType(mediaFolder);
        var semaphore = new SemaphoreSlim(Plugin.Instance.Configuration.VirtualFileSystemThreads);
        await Task.WhenAll(allFiles.Select(async (tuple) => {
            await semaphore.WaitAsync().ConfigureAwait(false);

            try {
                Logger.LogTrace("Generating links for {Path} (File={FileId},Series={SeriesId})", tuple.sourceLocation, tuple.fileId, tuple.seriesId);

                var (sourceLocation, symbolicLinks, importedAt) = await GenerateLocationsForFile(vfsPath, collectionType, tuple.sourceLocation, tuple.fileId, tuple.seriesId).ConfigureAwait(false);

                // Skip any source files we weren't meant to have in the library.
                if (string.IsNullOrEmpty(sourceLocation) || !importedAt.HasValue)
                    return;

                var subResult = GenerateSymbolicLinks(sourceLocation, symbolicLinks, importedAt.Value);

                // Combine the current results with the overall results.
                lock (semaphore) {
                    result += subResult;
                }
            }
            finally {
                semaphore.Release();
            }
        }))
            .ConfigureAwait(false);

        return result;
    }

    public async Task<(string sourceLocation, string[] symbolicLinks, DateTime? importedAt)> GenerateLocationsForFile(Folder mediaFolder, string sourceLocation, string fileId, string seriesId)
    {
        var vfsPath = ShokoAPIManager.GetVirtualRootForMediaFolder(mediaFolder);
        var collectionType = LibraryManager.GetInheritedContentType(mediaFolder);
        return await GenerateLocationsForFile(vfsPath, collectionType, sourceLocation, fileId, seriesId);
    }

    private async Task<(string sourceLocation, string[] symbolicLinks, DateTime? importedAt)> GenerateLocationsForFile(string vfsPath, CollectionType? collectionType, string sourceLocation, string fileId, string seriesId)
    {
        var season = await ApiManager.GetSeasonInfoForSeries(seriesId).ConfigureAwait(false);
        if (season == null)
            return (string.Empty, [], null);

        var isMovieSeason = season.Type == SeriesType.Movie;
        var shouldAbort = collectionType switch {
            CollectionType.tvshows => isMovieSeason && Plugin.Instance.Configuration.SeparateMovies,
            CollectionType.movies => !isMovieSeason,
            _ => false,
        };
        if (shouldAbort)
            return (string.Empty, [], null);

        var show = await ApiManager.GetShowInfoForSeries(seriesId).ConfigureAwait(false);
        if (show == null)
            return (string.Empty, [], null);

        var file = await ApiManager.GetFileInfo(fileId, seriesId).ConfigureAwait(false);
        var (episode, episodeXref, _) = (file?.EpisodeList ?? []).FirstOrDefault();
        if (file == null || episode == null)
            return (string.Empty, [], null);

        if (season == null || episode == null)
            return (string.Empty, [], null);

        var showName = show.DefaultSeason.AniDB.Title?.ReplaceInvalidPathCharacters() ?? $"Shoko Series {show.Id}";
        var episodeNumber = Ordering.GetEpisodeNumber(show, season, episode);
        var episodeName = (episode.AniDB.Titles.FirstOrDefault(t => t.LanguageCode == "en")?.Value ?? $"Episode {episode.AniDB.Type} {episodeNumber}").ReplaceInvalidPathCharacters();

        // For those **really** long names we have to cut if off at some point…
        if (showName.Length >= NameCutOff)
            showName = showName[..NameCutOff].Split(' ').SkipLast(1).Join(' ') + "…";
        if (episodeName.Length >= NameCutOff)
            episodeName = episodeName[..NameCutOff].Split(' ').SkipLast(1).Join(' ') + "…";

        var isExtra = file.EpisodeList.Any(eI => season.IsExtraEpisode(eI.Episode));
        var folders = new List<string>();
        var extrasFolder = file.ExtraType switch {
            null => isExtra ? "extras" : null,
            ExtraType.ThemeSong => "theme-music",
            ExtraType.ThemeVideo => "backdrops",
            ExtraType.Trailer => "trailers",
            _ => "extras",
        };
        var fileNameSuffix = file.ExtraType switch {
            ExtraType.BehindTheScenes => "-behindTheScenes",
            ExtraType.Clip => "-clip",
            ExtraType.DeletedScene => "-deletedScene",
            ExtraType.Interview => "-interview",
            ExtraType.Scene => "-scene",
            ExtraType.Sample => "-other",
            ExtraType.Unknown => "-other",
            ExtraType.ThemeSong => string.Empty,
            ExtraType.ThemeVideo => string.Empty,
            ExtraType.Trailer => string.Empty,
            _ => isExtra ? "-other" : string.Empty,
        };
        var filePartSuffix = (episodeXref.Percentage?.Size ?? 100) != 100 ? $".pt{episode.Shoko.CrossReferences.Where(xref => xref.ReleaseGroup == episodeXref.ReleaseGroup && xref.Percentage!.Size == episodeXref.Percentage!.Size).ToList().FindIndex(xref => xref.Percentage!.Start == episodeXref.Percentage!.Start && xref.Percentage!.End == episodeXref.Percentage!.End) + 1}" : "";
        if (isMovieSeason && collectionType != CollectionType.tvshows) {
            if (!string.IsNullOrEmpty(extrasFolder)) {
                foreach (var episodeInfo in season.EpisodeList)
                    folders.Add(Path.Join(vfsPath, $"{showName} [{ShokoSeriesId.Name}={show.Id}] [{ShokoEpisodeId.Name}={episodeInfo.Id}]", extrasFolder));
            }
            else {
                folders.Add(Path.Join(vfsPath, $"{showName} [{ShokoSeriesId.Name}={show.Id}] [{ShokoEpisodeId.Name}={episode.Id}]"));
                episodeName = "Movie";
            }
        }
        else {
            var isSpecial = show.IsSpecial(episode);
            var seasonNumber = Ordering.GetSeasonNumber(show, season, episode);
            var seasonFolder = $"Season {(isSpecial ? 0 : seasonNumber).ToString().PadLeft(2, '0')}";
            var showFolder = $"{showName} [{ShokoSeriesId.Name}={show.Id}]";
            if (!string.IsNullOrEmpty(extrasFolder)) {
                folders.Add(Path.Join(vfsPath, showFolder, extrasFolder));

                // Only place the extra within the season if we have a season number assigned to the episode.
                if (seasonNumber != 0)
                    folders.Add(Path.Join(vfsPath, showFolder, seasonFolder, extrasFolder));
            }
            else {
                folders.Add(Path.Join(vfsPath, showFolder, seasonFolder));
                episodeName = $"{showName} S{(isSpecial ? 0 : seasonNumber).ToString().PadLeft(2, '0')}E{episodeNumber.ToString().PadLeft(show.EpisodePadding, '0')}{filePartSuffix}";
            }
        }

        var fileName = $"{episodeName} [{ShokoSeriesId.Name}={seriesId}] [{ShokoFileId.Name}={fileId}]{fileNameSuffix}{Path.GetExtension(sourceLocation)}";
        var symbolicLinks = folders
            .Select(folderPath => Path.Join(folderPath, fileName))
            .ToArray();

        foreach (var symbolicLink in symbolicLinks)
            ApiManager.AddFileLookupIds(symbolicLink, fileId, seriesId, file.EpisodeList.Select(episode => episode.Id));
        return (sourceLocation, symbolicLinks, (file.Shoko.ImportedAt ?? file.Shoko.CreatedAt).ToLocalTime());
    }

    public LinkGenerationResult GenerateSymbolicLinks(string sourceLocation, string[] symbolicLinks, DateTime importedAt)
    {
        try {
            var result = new LinkGenerationResult();
            var sourcePrefixLength = sourceLocation.Length - Path.GetExtension(sourceLocation).Length;
            var subtitleLinks = FindSubtitlesForPath(sourceLocation);
            foreach (var symbolicLink in symbolicLinks) {
                var symbolicDirectory = Path.GetDirectoryName(symbolicLink)!;
                if (!Directory.Exists(symbolicDirectory))
                    Directory.CreateDirectory(symbolicDirectory);

                result.Paths.Add(symbolicLink);
                if (!File.Exists(symbolicLink)) {
                    result.CreatedVideos++;
                    Logger.LogDebug("Linking {Link} → {LinkTarget}", symbolicLink, sourceLocation);
                    File.CreateSymbolicLink(symbolicLink, sourceLocation);
                    // Mock the creation date to fake the "date added" order in Jellyfin.
                    File.SetCreationTime(symbolicLink, importedAt);
                }
                else {
                    var shouldFix = false;
                    try {
                        var nextTarget = File.ResolveLinkTarget(symbolicLink, false);
                        if (!string.Equals(sourceLocation, nextTarget?.FullName)) {
                            shouldFix = true;

                            Logger.LogWarning("Fixing broken symbolic link {Link} → {LinkTarget} (RealTarget={RealTarget})", symbolicLink, sourceLocation, nextTarget?.FullName);
                        }
                        var date = File.GetCreationTime(symbolicLink).ToLocalTime();
                        if (date != importedAt) {
                            shouldFix = true;

                            Logger.LogWarning("Fixing broken symbolic link {Link} with incorrect date.", symbolicLink);
                        }
                    }
                    catch (Exception ex) {
                        Logger.LogError(ex, "Encountered an error trying to resolve symbolic link {Link}", symbolicLink);
                        shouldFix = true;
                    }
                    if (shouldFix) {
                        File.Delete(symbolicLink);
                        File.CreateSymbolicLink(symbolicLink, sourceLocation);
                        // Mock the creation date to fake the "date added" order in Jellyfin.
                        File.SetCreationTime(symbolicLink, importedAt);
                        result.FixedVideos++;
                    }
                    else {
                        result.SkippedVideos++;
                    }
                }

                if (subtitleLinks.Count > 0) {
                    var symbolicName = Path.GetFileNameWithoutExtension(symbolicLink);
                    foreach (var subtitleSource in subtitleLinks) {
                        var extName = subtitleSource[sourcePrefixLength..];
                        var subtitleLink = Path.Join(symbolicDirectory, symbolicName + extName);

                        result.Paths.Add(subtitleLink);
                        if (!File.Exists(subtitleLink)) {
                            result.CreatedSubtitles++;
                            Logger.LogDebug("Linking {Link} → {LinkTarget}", subtitleLink, subtitleSource);
                            File.CreateSymbolicLink(subtitleLink, subtitleSource);
                        }
                        else {
                            var shouldFix = false;
                            try {
                                var nextTarget = File.ResolveLinkTarget(subtitleLink, false);
                                if (!string.Equals(subtitleSource, nextTarget?.FullName)) {
                                    shouldFix = true;

                                    Logger.LogWarning("Fixing broken symbolic link {Link} → {LinkTarget} (RealTarget={RealTarget})", subtitleLink, subtitleSource, nextTarget?.FullName);
                                }
                            }
                            catch (Exception ex) {
                                Logger.LogError(ex, "Encountered an error trying to resolve symbolic link {Link} for {LinkTarget}", subtitleLink, subtitleSource);
                                shouldFix = true;
                            }
                            if (shouldFix) {
                                File.Delete(subtitleLink);
                                File.CreateSymbolicLink(subtitleLink, subtitleSource);
                                result.FixedSubtitles++;
                            }
                            else {
                                result.SkippedSubtitles++;
                            }
                        }
                    }
                }
            }

            return result;
        }
        catch (Exception ex) {
            Logger.LogError(ex, "An error occurred while trying to generate {LinkCount} links for {SourceLocation}; {ErrorMessage}", symbolicLinks.Length, sourceLocation, ex.Message);
            throw;
        }
    }

    private List<string> FindSubtitlesForPath(string sourcePath)
    {
        var externalPaths = new List<string>();
        var folderPath = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(folderPath) || !FileSystem.DirectoryExists(folderPath))
            return externalPaths;

        var files = FileSystem.GetFilePaths(folderPath)
            .Except(new[] { sourcePath })
            .ToList();
        var sourcePrefix = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var file in files) {
            var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
            if (
                fileNameWithoutExtension.Length >= sourcePrefix.Length &&
                sourcePrefix.Equals(fileNameWithoutExtension[..sourcePrefix.Length], StringComparison.OrdinalIgnoreCase) &&
                (fileNameWithoutExtension.Length == sourcePrefix.Length || NamingOptions.MediaFlagDelimiters.Contains(fileNameWithoutExtension[sourcePrefix.Length]))
            ) {
                var externalPathInfo = ExternalPathParser.ParseFile(file, fileNameWithoutExtension[sourcePrefix.Length..].ToString());
                if (externalPathInfo != null && !string.IsNullOrEmpty(externalPathInfo.Path))
                    externalPaths.Add(externalPathInfo.Path);
            }
        }

        return externalPaths;
    }

    private LinkGenerationResult CleanupStructure(string vfsPath, string directoryToClean, IReadOnlyList<string> allKnownPaths)
    {
        // Search the selected paths for files to remove.
        Logger.LogDebug("Looking for files to remove in folder at {Path}", directoryToClean);
        var start = DateTime.Now;
        var previousStep = start;
        var result = new LinkGenerationResult();
        var searchFiles = NamingOptions.VideoFileExtensions.Concat(NamingOptions.SubtitleFileExtensions).Append(".nfo").ToHashSet();
        var toBeRemoved = FileSystem.GetFilePaths(directoryToClean, true)
            .Select(path => (path, extName: Path.GetExtension(path)))
            .Where(tuple => !string.IsNullOrEmpty(tuple.extName) && searchFiles.Contains(tuple.extName))
            .ExceptBy(allKnownPaths.ToHashSet(), tuple => tuple.path)
            .ToList();

        var nextStep = DateTime.Now;
        Logger.LogDebug("Found {FileCount} files to remove in {DirectoryToClean} in {TimeSpent}", toBeRemoved.Count, directoryToClean, nextStep - previousStep);
        previousStep = nextStep;

        foreach (var (location, extName) in toBeRemoved) {
            // NFOs.
            if (extName == ".nfo") {
                try {
                    Logger.LogTrace("Removing NFO file at {Path}", location);
                    File.Delete(location);
                }
                catch (Exception ex) {
                    Logger.LogError(ex, "Encountered an error trying to remove {FilePath}", location);
                    continue;
                }
                result.RemovedNfos++;
            }
            // Subtitle files.
            else if (NamingOptions.SubtitleFileExtensions.Contains(extName)) {
                // Try moving subtitle if possible, otherwise remove it. There is no in-between.
                if (TryMoveSubtitleFile(allKnownPaths, location)) {
                    result.FixedSubtitles++;
                }
                else {
                    try {
                        Logger.LogTrace("Removing subtitle file at {Path}", location);
                        File.Delete(location);
                    }
                    catch (Exception ex) {
                        Logger.LogError(ex, "Encountered an error trying to remove {FilePath}", location);
                        continue;
                    }
                    result.RemovedSubtitles++;
                    
                }
            }
            // Video files.
            else {
                if (ShouldIgnoreVideo(vfsPath, location)) {
                    result.SkippedVideos++;
                }
                else {
                    try {
                        Logger.LogTrace("Removing video file at {Path}", location);
                        File.Delete(location);
                    }
                    catch (Exception ex) {
                        Logger.LogError(ex, "Encountered an error trying to remove {FilePath}", location);
                        continue;
                    }
                    result.RemovedVideos++;
                }
            }
        }

        nextStep = DateTime.Now;
        Logger.LogTrace("Removed {FileCount} files in {DirectoryToClean} in {TimeSpent} (Total={TotalSpent})", result.Removed, directoryToClean, nextStep - previousStep, nextStep - start);
        previousStep = nextStep;

        var cleaned = 0;
        var directoriesToClean = toBeRemoved
            .SelectMany(tuple => {
                var path = Path.GetDirectoryName(tuple.path);
                var paths = new List<(string path, int level)>();
                while (!string.IsNullOrEmpty(path)) {
                    var level = path == directoryToClean ? 0 : path[(directoryToClean.Length + 1)..].Split(Path.DirectorySeparatorChar).Length;
                    paths.Add((path, level));
                    if (path == directoryToClean)
                        break;
                    path = Path.GetDirectoryName(path);
                }
                return paths;
            })
            .DistinctBy(tuple => tuple.path)
            .OrderByDescending(tuple => tuple.level)
            .ThenBy(tuple => tuple.path)
            .Select(tuple => tuple.path)
            .ToList();

        nextStep = DateTime.Now;
        Logger.LogDebug("Found {DirectoryCount} directories to potentially clean in {DirectoryToClean} in {TimeSpent} (Total={TotalSpent})", toBeRemoved.Count, directoryToClean, nextStep - previousStep, nextStep - start);
        previousStep = nextStep;

        foreach (var directoryPath in directoriesToClean) {
            if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any()) {
                Logger.LogTrace("Removing empty directory at {Path}", directoryPath);
                Directory.Delete(directoryPath);
                cleaned++;
            }
        }

        Logger.LogTrace("Cleaned {CleanedCount} directories in {DirectoryToClean} in {TimeSpent} (Total={TotalSpent})", cleaned, directoriesToClean, nextStep - previousStep, nextStep - start);

        return result;
    }

    private static bool TryMoveSubtitleFile(IReadOnlyList<string> allKnownPaths, string subtitlePath)
    {
        if (!TryGetIdsForPath(subtitlePath, out var seriesId, out var fileId))
            return false;

        var symbolicLink = allKnownPaths.FirstOrDefault(knownPath =>
            TryGetIdsForPath(knownPath, out var knownSeriesId, out var knownFileId) && seriesId == knownSeriesId && fileId == knownFileId
        );
        if (string.IsNullOrEmpty(symbolicLink))
            return false;

        var sourcePathWithoutExt = symbolicLink[..^Path.GetExtension(symbolicLink).Length];
        if (!subtitlePath.StartsWith(sourcePathWithoutExt))
            return false;

        var extName = subtitlePath[sourcePathWithoutExt.Length..];
        string? realTarget = null;
        try {
            realTarget = File.ResolveLinkTarget(symbolicLink, false)?.FullName;
        }
        catch { }
        if (string.IsNullOrEmpty(realTarget))
            return false;

        var realSubtitlePath = realTarget[..^Path.GetExtension(realTarget).Length] + extName;
        if (!File.Exists(realSubtitlePath))
            File.Move(subtitlePath, realSubtitlePath);
        else
            File.Delete(subtitlePath);
        File.CreateSymbolicLink(subtitlePath, realSubtitlePath);

        return true;
    }

    private static bool ShouldIgnoreVideo(string vfsPath, string path)
    {
        // Ignore the video if it's within one of the folders to potentially ignore _and_ it doesn't have any shoko ids set.
        var parentDirectories = path[(vfsPath.Length + 1)..].Split(Path.DirectorySeparatorChar).SkipLast(1).ToArray();
        return parentDirectories.Length > 1 && IgnoreFolderNames.Contains(parentDirectories.Last()) && !TryGetIdsForPath(path, out _, out _);
    }

    private static bool TryGetIdsForPath(string path, [NotNullWhen(true)] out string? seriesId, [NotNullWhen(true)] out string? fileId)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!fileName.TryGetAttributeValue(ShokoFileId.Name, out fileId) || !int.TryParse(fileId, out _)) {
            seriesId = null;
            fileId = null;
            return false;
        }

        if (!fileName.TryGetAttributeValue(ShokoSeriesId.Name, out seriesId) || !int.TryParse(seriesId, out _)) {
            seriesId = null;
            fileId = null;
            return false;
        }

        return true;
    }

    #endregion

    #region Resolvers

    public async Task<BaseItem?> ResolveSingle(Folder? parent, CollectionType? collectionType, FileSystemMetadata fileInfo)
    {
        if (!(collectionType == CollectionType.tvshows || collectionType == CollectionType.movies || collectionType == null) || parent == null || fileInfo == null)
            return null;

        var root = LibraryManager.RootFolder;
        if (root == null || parent == root)
            return null;

        try {
            if (!Lookup.IsEnabledForItem(parent))
                return null;

            // Skip anything outside the VFS.
            if (!fileInfo.FullName.StartsWith(Plugin.Instance.VirtualRoot))
                return null;

            if (parent.GetTopParent() is not Folder mediaFolder)
                return null;

            var vfsPath = await GenerateStructureInVFS(mediaFolder, fileInfo.FullName).ConfigureAwait(false);
            if (string.IsNullOrEmpty(vfsPath))
                return null;

            if (parent.Id == mediaFolder.Id && fileInfo.IsDirectory) {
                if (!fileInfo.Name.TryGetAttributeValue(ShokoSeriesId.Name, out var seriesId) || !int.TryParse(seriesId, out _))
                    return null;

                return new TvSeries() {
                    Path = fileInfo.FullName,
                };
            }

            return null;
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Threw unexpectedly; {Message}", ex.Message);
            throw;
        }
    }

    public async Task<MultiItemResolverResult?> ResolveMultiple(Folder? parent, CollectionType? collectionType, List<FileSystemMetadata> fileInfoList)
    {
        if (!(collectionType == CollectionType.tvshows || collectionType == CollectionType.movies || collectionType == null) || parent == null)
            return null;

        var root = LibraryManager.RootFolder;
        if (root == null || parent == root)
            return null;

        try {
            if (!Lookup.IsEnabledForItem(parent))
                return null;

            if (parent.GetTopParent() is not Folder mediaFolder)
                return null;

            var vfsPath = await GenerateStructureInVFS(mediaFolder, parent.Path).ConfigureAwait(false);
            if (string.IsNullOrEmpty(vfsPath))
                return null;

            // Redirect children of a VFS managed media folder to the VFS.
            if (parent.IsTopParent) {
                var createMovies = collectionType == CollectionType.movies || (collectionType == null && Plugin.Instance.Configuration.SeparateMovies);
                var items = FileSystem.GetDirectories(vfsPath)
                    .AsParallel()
                    .SelectMany(dirInfo => {
                        if (!dirInfo.Name.TryGetAttributeValue(ShokoSeriesId.Name, out var seriesId) || !int.TryParse(seriesId, out _))
                            return Array.Empty<BaseItem>();

                        var season = ApiManager.GetSeasonInfoForSeries(seriesId)
                            .ConfigureAwait(false)
                            .GetAwaiter()
                            .GetResult();
                        if (season == null)
                            return Array.Empty<BaseItem>();

                        if (createMovies && season.Type == SeriesType.Movie) {
                            return FileSystem.GetFiles(dirInfo.FullName)
                                .AsParallel()
                                .Select(fileInfo => {
                                    // Only allow the video files, since the subtitle files also have the ids set.
                                    if (!NamingOptions.VideoFileExtensions.Contains(Path.GetExtension(fileInfo.Name)))
                                        return null;

                                    if (!TryGetIdsForPath(fileInfo.FullName, out seriesId, out var fileId))
                                        return null;

                                    // This will hopefully just re-use the pre-cached entries from the cache, but it may
                                    // also get it from remote if the cache was emptied for whatever reason.
                                    var file = ApiManager.GetFileInfo(fileId, seriesId)
                                        .ConfigureAwait(false)
                                        .GetAwaiter()
                                        .GetResult();

                                    // Abort if the file was not recognized.
                                    if (file == null || file.EpisodeList.Any(eI => season.IsExtraEpisode(eI.Episode)))
                                        return null;

                                    return new Movie() {
                                        Path = fileInfo.FullName,
                                    } as BaseItem;
                                })
                                .ToArray();
                        }

                        return new BaseItem[1] {
                            new TvSeries() {
                                Path = dirInfo.FullName,
                            },
                        };
                    })
                    .OfType<BaseItem>()
                    .ToList();

                // TODO: uncomment the code snippet once the PR is in stable JF.
                // return new() { Items = items, ExtraFiles = new() };

                // TODO: Remove these two hacks once we have proper support for adding multiple series at once.
                if (!items.Any(i => i is Movie) && items.Count > 0) {
                    fileInfoList.Clear();
                    fileInfoList.AddRange(items.OrderBy(s => int.Parse(s.Path.GetAttributeValue(ShokoSeriesId.Name)!)).Select(s => FileSystem.GetFileSystemInfo(s.Path)));
                }

                return new() { Items = items.Where(i => i is Movie).ToList(), ExtraFiles = items.OfType<TvSeries>().Select(s => FileSystem.GetFileSystemInfo(s.Path)).ToList() };
            }

            return null;
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Threw unexpectedly; {Message}", ex.Message);
            throw;
        }
    }

    #endregion

    #region Ignore Rule

    public async Task<bool> ShouldFilterItem(Folder? parent, FileSystemMetadata fileInfo)
    {
        // Check if the parent is not made yet, or the file info is missing.
        if (parent == null || fileInfo == null)
            return false;

        // Check if the root is not made yet. This should **never** be false at
        // this point in time, but if it is, then bail.
        var root = LibraryManager.RootFolder;
        if (root == null || parent.Id == root.Id)
            return false;

        // Assume anything within the VFS is already okay.
        if (fileInfo.FullName.StartsWith(Plugin.Instance.VirtualRoot))
            return false;

        try {
            // Enable the scanner if we selected to use the Shoko provider for any metadata type on the current root folder.
            if (!Lookup.IsEnabledForItem(parent, out var isSoleProvider))
                return false;

            if (fileInfo.IsDirectory && Plugin.Instance.IgnoredFolders.Contains(Path.GetFileName(fileInfo.FullName).ToLowerInvariant())) {
                Logger.LogDebug("Excluded folder at path {Path}", fileInfo.FullName);
                return true;
            }

            if (!fileInfo.IsDirectory && !NamingOptions.VideoFileExtensions.Contains(fileInfo.Extension.ToLowerInvariant())) {
                Logger.LogDebug("Skipped excluded file at path {Path}", fileInfo.FullName);
                return false;
            }

            var fullPath = fileInfo.FullName;
            var (mediaFolder, partialPath) = ApiManager.FindMediaFolder(fullPath, parent, root);

            // Ignore any media folders that aren't mapped to shoko.
            var mediaFolderConfig = await GetOrCreateConfigurationForMediaFolder(mediaFolder);
            if (!mediaFolderConfig.IsMapped) {
                Logger.LogDebug("Skipped media folder for path {Path} (MediaFolder={MediaFolderId})", fileInfo.FullName, mediaFolderConfig.MediaFolderId);
                return false;
            }

            // Filter out anything in the media folder if the VFS is enabled,
            // because the VFS is pre-filtered, and we should **never** reach
            // this point except for the folders in the root of the media folder
            // that we're not even going to use.
            if (mediaFolderConfig.IsVirtualFileSystemEnabled)
                return true;

            var shouldIgnore = mediaFolderConfig.LibraryFilteringMode switch {
                Ordering.LibraryFilteringMode.Strict => true,
                Ordering.LibraryFilteringMode.Lax => false,
                // Ordering.LibraryFilteringMode.Auto =>
                _ => mediaFolderConfig.IsVirtualFileSystemEnabled  || isSoleProvider,
            };
            var collectionType = LibraryManager.GetInheritedContentType(mediaFolder);
            if (fileInfo.IsDirectory)
                return await ShouldFilterDirectory(partialPath, fullPath, collectionType, shouldIgnore).ConfigureAwait(false);
            else
                return await ShouldFilterFile(partialPath, fullPath, shouldIgnore).ConfigureAwait(false);
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Threw unexpectedly; {Message}", ex.Message);
            throw;
        }
    }

    private async Task<bool> ShouldFilterDirectory(string partialPath, string fullPath, CollectionType? collectionType, bool shouldIgnore)
    {
        var season = await ApiManager.GetSeasonInfoByPath(fullPath).ConfigureAwait(false);

        // We inform/warn here since we enabled the provider in our library, but we can't find a match for the given folder path.
        if (season == null) {
            // If we're in strict mode, then check the sub-directories if we have a <Show>/<Season>/<Episodes> structure.
            if (shouldIgnore && partialPath[1..].Split(Path.DirectorySeparatorChar).Length == 1) {
                try {
                    var entries = FileSystem.GetDirectories(fullPath, false).ToList();
                    Logger.LogDebug("Unable to find shoko series for {Path}, trying {DirCount} sub-directories.", partialPath, entries.Count);
                    foreach (var entry in entries) {
                        season = await ApiManager.GetSeasonInfoByPath(entry.FullName).ConfigureAwait(false);
                        if (season != null) {
                            Logger.LogDebug("Found shoko series {SeriesName} for sub-directory of path {Path} (Series={SeriesId})", season.Shoko.Name, partialPath, season.Id);
                            break;
                        }
                    }
                }
                catch (DirectoryNotFoundException) { }
            }
            if (season == null) {
                if (shouldIgnore)
                    Logger.LogInformation("Ignored unknown folder at path {Path}", partialPath);
                else
                    Logger.LogWarning("Skipped unknown folder at path {Path}", partialPath);
                return shouldIgnore;
            }
        }

        // Filter library if we enabled the option.
        var isMovieSeason = season.Type == SeriesType.Movie;
        switch (collectionType) {
            case CollectionType.tvshows:
                if (isMovieSeason && Plugin.Instance.Configuration.SeparateMovies) {
                    Logger.LogInformation("Found movie in show library and library separation is enabled, ignoring shoko series. (Series={SeriesId})", season.Id);
                    return true;
                }
                break;
            case CollectionType.movies:
                if (!isMovieSeason) {
                    Logger.LogInformation("Found show in movie library, ignoring shoko series. (Series={SeriesId})", season.Id);
                    return true;
                }
                break;
        }

        var show = await ApiManager.GetShowInfoForSeries(season.Id).ConfigureAwait(false)!;
        if (!string.IsNullOrEmpty(show!.GroupId))
            Logger.LogInformation("Found shoko group {GroupName} (Series={SeriesId},Group={GroupId})", show.Name, season.Id, show.GroupId);
        else
            Logger.LogInformation("Found shoko series {SeriesName} (Series={SeriesId})", season.Shoko.Name, season.Id);

        return false;
    }

    private async Task<bool> ShouldFilterFile(string partialPath, string fullPath, bool shouldIgnore)
    {
        var (file, season, _) = await ApiManager.GetFileInfoByPath(fullPath).ConfigureAwait(false);

        // We inform/warn here since we enabled the provider in our library, but we can't find a match for the given file path.
        if (file == null || season == null) {
            if (shouldIgnore)
                Logger.LogInformation("Ignored unknown file at path {Path}", partialPath);
            else
                Logger.LogWarning("Skipped unknown file at path {Path}", partialPath);
            return shouldIgnore;
        }

        Logger.LogInformation("Found {EpisodeCount} shoko episode(s) for {SeriesName} (Series={SeriesId},File={FileId})", file.EpisodeList.Count, season.Shoko.Name, season.Id, file.Id);

        // We're going to post process this file later, but we don't want to include it in our library for now.
        if (file.EpisodeList.Any(eI => season.IsExtraEpisode(eI.Episode))) {
            Logger.LogInformation("File was assigned an extra type, ignoring file. (Series={SeriesId},File={FileId})", season.Id, file.Id);
            return true;
        }

        return false;
    }

    #endregion
}
