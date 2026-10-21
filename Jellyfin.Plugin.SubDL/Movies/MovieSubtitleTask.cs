using System.Globalization;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SubDL.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL.Movies;

/// <summary>
/// Downloads several Persian subtitles per movie, one per uploader, for movies that have none.
/// Newest movies go first, and each movie is filled before moving on to the next.
/// </summary>
public class MovieSubtitleTask : IScheduledTask
{
    // subdl documents >= 0.8 as a confident release match.
    private const double PerfectMatchScore = 0.8;

    private const string SubSourceUploader = "SubSource";

    // Every format SubtitleHelpers.ExtractAsync can return (srt, ass, ssa, vtt) is three ASCII
    // letters, so a name built with this one has the same length as the final name.
    private const string LengthProbeFormat = "srt";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly SubdlClient _subdl;
    private readonly SubSourceClient _subSource;
    private readonly ILogger<MovieSubtitleTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieSubtitleTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaSourceManager">Instance of the <see cref="IMediaSourceManager"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="subdl">The shared subdl client.</param>
    /// <param name="subSource">The shared SubSource client.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public MovieSubtitleTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        SubdlClient subdl,
        SubSourceClient subSource,
        ILogger<MovieSubtitleTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _subdl = subdl;
        _subSource = subSource;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Download Persian subtitles";

    /// <inheritdoc />
    public string Key => "SubDLMovieSubtitles";

    /// <inheritdoc />
    public string Description =>
        "Downloads Persian subtitles from subdl, one per uploader, for movies that have none, up to the "
        + "\"Persian subtitles per movie\" setting. Does nothing while that setting is 0.";

    /// <inheritdoc />
    public string Category => "SubDL";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (plugin is null || config is null || config.SubtitlesPerMovie <= 0 || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return;
        }

        // Kept beside the plugin's settings file rather than in its data folder, which sits inside
        // the plugins directory the plugin manager scans and replaces on upgrade.
        var record = SearchRecord.Load(Path.Combine(
            Path.GetDirectoryName(plugin.ConfigurationFilePath)!,
            "SubDL.finished-movies.json"));

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Recursive = true
        });

        var moviesDone = 0;
        var filesWritten = 0;
        var quotaStopped = false;

        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)index / items.Count * 100);

            if (items[index] is not Video movie || ShouldSkip(movie, record))
            {
                continue;
            }

            var outcome = await ProcessMovieAsync(movie, config.SubtitlesPerMovie, config.ApiKey, config.SubSourceApiKey, cancellationToken)
                .ConfigureAwait(false);

            filesWritten += outcome.Written;
            if (outcome.Written > 0)
            {
                // Jellyfin only notices new external subtitle files on a refresh.
                _providerManager.QueueRefresh(
                    movie.Id,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                    RefreshPriority.Normal);
            }

            if (outcome.QuotaSpent)
            {
                // The movie stays unfinished so the next run tops it up.
                quotaStopped = true;
                break;
            }

            if (outcome.Finished)
            {
                MarkFinished(record, movie);
                moviesDone++;
            }
        }

        progress.Report(100);

        _logger.LogInformation(
            "SubDL: movie subtitle run done: {Movies} movie(s) finished, {Files} file(s) written{Quota}",
            moviesDone,
            filesWritten,
            quotaStopped ? ", stopped early because the daily download quota is spent" : string.Empty);
    }

    private static string Stem(Video movie) => Path.GetFileNameWithoutExtension(movie.Path);

    private bool ShouldSkip(Video movie, SearchRecord record)
    {
        // .strm files, disc rips and alternate versions have no single video file to name subtitles after.
        if (movie.IsShortcut)
        {
            _logger.LogDebug("SubDL: {Movie} is a shortcut (.strm), skipping", movie.Name);
            return true;
        }

        if (movie.VideoType != VideoType.VideoFile || string.IsNullOrEmpty(movie.Path))
        {
            _logger.LogDebug("SubDL: {Movie} is not a single video file ({Type}), skipping", movie.Name, movie.VideoType);
            return true;
        }

        if (movie.PrimaryVersionId is { } primary && primary != Guid.Empty)
        {
            _logger.LogDebug("SubDL: {Movie} is an alternate version, skipping", movie.Name);
            return true;
        }

        if (record.IsFinished(movie.Id))
        {
            return true;
        }

        var streams = _mediaSourceManager.GetMediaStreams(movie.Id);

        // A movie the scan has added but not yet probed has no streams at all, so an embedded
        // Persian subtitle would go unseen. Leave it unfinished for a later run.
        if (!streams.Any(s => s.Type == MediaStreamType.Video))
        {
            _logger.LogDebug("SubDL: {Movie} has not been probed yet, skipping for now", movie.Name);
            return true;
        }

        var stem = Stem(movie);
        var foreign = streams
            .FirstOrDefault(s => s.Type == MediaStreamType.Subtitle
                && SubtitleHelpers.IsPersianCode(s.Language)
                && !IsOurs(s, stem));
        if (foreign is not null)
        {
            _logger.LogDebug("SubDL: {Movie} already has a Persian subtitle, skipping", movie.Name);
            return true;
        }

        return false;
    }

    private static bool IsOurs(MediaStream stream, string stem)
        => stream.IsExternal
            && !string.IsNullOrEmpty(stream.Path)
            && SubtitleNaming.TryGetUploader(stem, Path.GetFileName(stream.Path), out _);

    private async Task<MovieOutcome> ProcessMovieAsync(
        Video movie,
        int perMovie,
        string apiKey,
        string? subSourceKey,
        CancellationToken cancellationToken)
    {
        var options = _libraryManager.GetLibraryOptions(movie);

        // Chosen before searching, so a folder we cannot write to never costs a download.
        var folder = ChooseFolder(movie, options);
        if (folder is null)
        {
            return new MovieOutcome(0, Finished: false, QuotaSpent: false);
        }

        var stem = Stem(movie);
        var have = FindOurUploaders(movie, stem, folder);

        var wanted = perMovie - have.Count;
        if (wanted <= 0)
        {
            _logger.LogInformation("SubDL: {Movie} already has {Count} of our subtitle(s)", movie.Name, have.Count);
            return new MovieOutcome(0, Finished: true, QuotaSpent: false);
        }

        var fileName = Path.GetFileName(movie.Path);
        IReadOnlyList<SubdlResult>? results;
        try
        {
            results = await _subdl.SearchAsync(fileName, false, apiKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "SubDL: search for {File} failed", fileName);
            results = null;
        }

        if (results is null)
        {
            // The search call failed; leave the movie unfinished so the next run tries again,
            // and carry on with the next movie so one bad title cannot block the backlog.
            return new MovieOutcome(0, Finished: false, QuotaSpent: false);
        }

        var candidates = results
            .Where(r => !options.RequirePerfectSubtitleMatch || r.MatchScore >= PerfectMatchScore)
            .Select(r => new SubtitleCandidate(r.NId, r.ReleaseName ?? "SubDL", r.Author, r.MatchScore))
            .ToList();

        // Nothing usable from subdl, whether it had no results or the perfect-match filter
        // removed them all: SubSource is the backup either way.
        if (candidates.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(subSourceKey))
            {
                _logger.LogInformation(
                    "SubDL: no Persian subtitles for {Movie}{Filtered}",
                    movie.Name,
                    results.Count > 0 ? " that pass the perfect-match filter" : string.Empty);
                return new MovieOutcome(0, Finished: true, QuotaSpent: false);
            }

            return await FromSubSourceAsync(movie, stem, folder, have, wanted, subSourceKey, cancellationToken).ConfigureAwait(false);
        }

        var picks = SubtitlePicker.Pick(candidates, have, wanted);

        _logger.LogInformation(
            "SubDL: {Movie}: {Results} result(s), downloading {Picks} subtitle(s)",
            movie.Name,
            results.Count,
            picks.Count);

        var count = 0;
        var writeFailed = false;
        var downloadFailed = false;
        foreach (var pick in picks)
        {
            var uploader = pick.Uploader ?? string.Empty;
            if (!NameFits(movie, stem, pick.Title, uploader, "SubDL"))
            {
                continue;
            }

            if (!await _subdl.HasDownloadQuotaAsync(apiKey, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning("SubDL: daily download quota is spent, stopping until the next run");
                return new MovieOutcome(count, Finished: false, QuotaSpent: true);
            }

            HttpResponseMessage download;
            try
            {
                download = await _subdl.DownloadAsync(pick.Id, apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "SubDL: download {Id} for {Movie} failed", pick.Id, movie.Name);
                downloadFailed = true;
                continue;
            }

            using var response = download;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SubDL: download {Id} for {Movie} failed with {Status}",
                    pick.Id,
                    movie.Name,
                    (int)response.StatusCode);
                downloadFailed = true;

                // A refused download may mean the quota ran out sooner than the cached count
                // says, so ask subdl again rather than refusing every remaining pick.
                _subdl.ExpireQuotaCache();
                if (!await _subdl.HasDownloadQuotaAsync(apiKey, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogWarning("SubDL: daily download quota is spent, stopping until the next run");
                    return new MovieOutcome(count, Finished: false, QuotaSpent: true);
                }

                continue;
            }

            _subdl.NoteDownloadUsed();

            var saved = await SaveAsync(response, movie, stem, folder, pick.Title, uploader, "SubDL", cancellationToken).ConfigureAwait(false);
            count += saved == SaveResult.Written ? 1 : 0;
            writeFailed |= saved == SaveResult.WriteFailed;
            downloadFailed |= saved == SaveResult.DownloadFailed;
        }

        // A failed write or download is not the sources running dry, so the movie stays
        // unfinished and the next run tries the missing uploaders again.
        return new MovieOutcome(count, Finished: !writeFailed && !downloadFailed, QuotaSpent: false);
    }

    private async Task<MovieOutcome> FromSubSourceAsync(
        Video movie,
        string stem,
        string folder,
        IReadOnlySet<string> have,
        int wanted,
        string apiKey,
        CancellationToken cancellationToken)
    {
        string? imdb = null;
        foreach (var pair in movie.ProviderIds)
        {
            if (pair.Key.Equals("Imdb", StringComparison.OrdinalIgnoreCase))
            {
                imdb = pair.Value;
                break;
            }
        }

        IReadOnlyList<SubSourceResult>? results;
        try
        {
            results = await _subSource.SearchAsync(imdb, movie.Name, movie.ProductionYear, false, null, null, apiKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "SubSource: search for {Movie} failed", movie.Name);
            results = null;
        }

        if (results is null)
        {
            // Same as a failed subdl search: try this movie again next run.
            return new MovieOutcome(0, Finished: false, QuotaSpent: false);
        }

        IReadOnlyList<SubtitleCandidate> picks;
        if (results.Any(r => !string.IsNullOrWhiteSpace(r.Uploader)))
        {
            // SubSource has no match score, so its own list order decides within an uploader.
            picks = SubtitlePicker.Pick(
                results.Select(r => new SubtitleCandidate(r.Id, r.ReleaseName ?? SubSourceUploader, r.Uploader, 0)),
                have,
                wanted);
        }
        else
        {
            // Without uploaders, grouping would collapse everything into one "unknown" file,
            // so take distinct release names instead and name them all after SubSource.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            picks = results
                .Where(r => seen.Add(SubtitleNaming.Clean(r.ReleaseName)))
                .Take(wanted)
                .Select(r => new SubtitleCandidate(r.Id, r.ReleaseName ?? SubSourceUploader, SubSourceUploader, 0))
                .ToList();
        }

        _logger.LogInformation(
            "SubSource: {Movie}: {Results} result(s), downloading {Picks} subtitle(s)",
            movie.Name,
            results.Count,
            picks.Count);

        var count = 0;
        var writeFailed = false;
        var downloadFailed = false;
        foreach (var pick in picks)
        {
            var uploader = pick.Uploader ?? SubSourceUploader;
            if (!NameFits(movie, stem, pick.Title, uploader, "SubSource"))
            {
                continue;
            }

            HttpResponseMessage download;
            try
            {
                download = await _subSource.DownloadAsync(pick.Id, apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
            {
                _logger.LogWarning(ex, "SubSource: download {Id} for {Movie} failed", pick.Id, movie.Name);
                downloadFailed = true;
                continue;
            }

            using var response = download;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SubSource: download {Id} for {Movie} failed with {Status}",
                    pick.Id,
                    movie.Name,
                    (int)response.StatusCode);
                downloadFailed = true;
                continue;
            }

            var saved = await SaveAsync(response, movie, stem, folder, pick.Title, uploader, "SubSource", cancellationToken).ConfigureAwait(false);
            count += saved == SaveResult.Written ? 1 : 0;
            writeFailed |= saved == SaveResult.WriteFailed;
            downloadFailed |= saved == SaveResult.DownloadFailed;
        }

        return new MovieOutcome(count, Finished: !writeFailed && !downloadFailed, QuotaSpent: false);
    }

    /// <summary>
    /// Picks the folder the movie's subtitles go to: beside the video when the library saves
    /// subtitles with media and that folder is writable, otherwise the item's metadata folder
    /// (a read-only media mount still gets its subtitles that way).
    /// </summary>
    /// <returns>The folder, or null when neither can be written to.</returns>
    private string? ChooseFolder(Video movie, LibraryOptions options)
    {
        if (options.SaveSubtitlesWithMedia && !string.IsNullOrEmpty(movie.ContainingFolderPath))
        {
            if (IsWritable(movie.ContainingFolderPath))
            {
                return movie.ContainingFolderPath;
            }

            _logger.LogInformation(
                "SubDL: {Folder} is not writable, saving subtitles for {Movie} in the metadata folder instead",
                movie.ContainingFolderPath,
                movie.Name);
        }

        var metadata = movie.GetInternalMetadataPath();
        if (!string.IsNullOrEmpty(metadata))
        {
            try
            {
                Directory.CreateDirectory(metadata);
                if (IsWritable(metadata))
                {
                    return metadata;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "SubDL: could not create {Folder}", metadata);
            }
        }

        // Searching now would spend downloads on files that cannot be saved; the movie stays
        // unfinished and is tried again once a folder is writable.
        _logger.LogWarning("SubDL: no writable folder for {Movie}'s subtitles, skipping it for now", movie.Name);
        return null;
    }

    /// <summary>
    /// Tells whether a file can be created in a folder by creating and removing a uniquely
    /// named probe file. Permission bits alone miss read-only mounts.
    /// </summary>
    private bool IsWritable(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        var probe = Path.Combine(
            folder,
            ".subdl-write-probe-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "SubDL: {Folder} is not writable", folder);
            return false;
        }

        try
        {
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "SubDL: could not remove the probe file {File}", probe);
        }

        return true;
    }

    /// <summary>
    /// Collects the uploaders the movie already has a file from: those Jellyfin knows about, and
    /// those on disk in the save folder that a refresh has not picked up yet.
    /// </summary>
    private HashSet<string> FindOurUploaders(Video movie, string stem, string folder)
    {
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stream in _mediaSourceManager.GetMediaStreams(movie.Id))
        {
            if (stream.Type == MediaStreamType.Subtitle
                && stream.IsExternal
                && !string.IsNullOrEmpty(stream.Path)
                && SubtitleNaming.TryGetUploader(stem, Path.GetFileName(stream.Path), out var uploader))
            {
                have.Add(SubtitlePicker.UploaderKey(uploader));
            }
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (SubtitleNaming.TryGetUploader(stem, Path.GetFileName(path), out var uploader))
                {
                    have.Add(SubtitlePicker.UploaderKey(uploader));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The database streams are still a usable answer; CreateNew guards against duplicates.
            _logger.LogDebug(ex, "SubDL: could not list {Folder}", folder);
        }

        return have;
    }

    /// <summary>
    /// Checks a pick's file name before it is downloaded. The name builder only shortens the
    /// title and uploader, so a very long video name can still exceed what the file system takes,
    /// and downloading it would spend quota on a file that can never be written.
    /// </summary>
    private bool NameFits(Video movie, string stem, string title, string uploader, string source)
    {
        var name = SubtitleNaming.BuildFileName(stem, title, uploader, LengthProbeFormat);
        if (SubtitleNaming.FitsFileSystem(name))
        {
            return true;
        }

        _logger.LogWarning(
            "{Source}: the subtitle name for {Movie} would be longer than {Limit} bytes, skipping it (shorten the video file name to fix this)",
            source,
            movie.Name,
            SubtitleNaming.FileSystemNameLimit);
        return false;
    }

    /// <summary>
    /// Tells whether an exception is a failed or timed-out call rather than the run being
    /// cancelled. HttpClient reports its own timeout as a TaskCanceledException.
    /// </summary>
    private static bool IsNetworkFailure(Exception ex, CancellationToken cancellationToken)
        => ex is HttpRequestException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>Extracts the subtitle from the zip and writes it, never over an existing file.</summary>
    /// <returns>Whether the file was written, skipped, or could not be downloaded or written.</returns>
    private async Task<SaveResult> SaveAsync(
        HttpResponseMessage response,
        Video movie,
        string stem,
        string folder,
        string title,
        string uploader,
        string source,
        CancellationToken cancellationToken)
    {
        MediaBrowser.Controller.Subtitles.SubtitleResponse subtitle;
        try
        {
            var archive = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            subtitle = await SubtitleHelpers.ExtractAsync(archive, "fa", null, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            // The connection dropped mid-download; worth trying again next run.
            _logger.LogWarning(ex, "{Source}: download for {Movie} broke off", source, movie.Name);
            return SaveResult.DownloadFailed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // A broken or empty archive will be just as broken next time, so the movie still finishes.
            _logger.LogWarning(ex, "{Source}: could not read the subtitle archive for {Movie}", source, movie.Name);
            return SaveResult.Skipped;
        }

        await using (subtitle.Stream.ConfigureAwait(false))
        {
            var name = SubtitleNaming.BuildFileName(stem, title, uploader, subtitle.Format);
            var path = Path.Combine(folder, name);

            var created = false;
            var completed = false;
            try
            {
                Directory.CreateDirectory(folder);

                // CreateNew: the plugin never overwrites a subtitle that is already there.
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                created = true;
                await using (file.ConfigureAwait(false))
                {
                    await subtitle.Stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                completed = true;
            }
            catch (IOException) when (!created && File.Exists(path))
            {
                _logger.LogDebug("{Source}: {File} already exists, skipping", source, name);
                return SaveResult.Skipped;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "{Source}: could not write {File} for {Movie}", source, name, movie.Name);
                return SaveResult.WriteFailed;
            }
            finally
            {
                if (created && !completed)
                {
                    DeletePartial(path);
                }
            }

            _logger.LogInformation("{Source}: saved {File} for {Movie}", source, name, movie.Name);
            return SaveResult.Written;
        }
    }

    /// <summary>
    /// Removes a file this run created but could not finish. Left behind, its name would make
    /// the next run count it as a finished subtitle from that uploader.
    /// </summary>
    private void DeletePartial(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "SubDL: could not remove the partly written {File}", path);
        }
    }

    private void MarkFinished(SearchRecord record, Video movie)
    {
        try
        {
            record.MarkFinished(movie.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the movie is only searched again on the next run.
            _logger.LogWarning(ex, "SubDL: could not record {Movie} as finished", movie.Name);
        }
    }

    private readonly record struct MovieOutcome(int Written, bool Finished, bool QuotaSpent);

    private enum SaveResult
    {
        /// <summary>The subtitle file was written.</summary>
        Written,

        /// <summary>Nothing written, and nothing to retry: the file exists or the archive was bad.</summary>
        Skipped,

        /// <summary>The file could not be written, so the movie should be tried again.</summary>
        WriteFailed,

        /// <summary>The download broke off, so the movie should be tried again.</summary>
        DownloadFailed
    }
}
