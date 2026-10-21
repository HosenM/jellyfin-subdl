using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SubDL.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
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

        var record = SearchRecord.Load(Path.Combine(plugin.DataFolderPath, "finished-movies.json"));

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
        if (movie.IsShortcut
            || movie.VideoType != VideoType.VideoFile
            || (movie.PrimaryVersionId is { } primary && primary != Guid.Empty)
            || string.IsNullOrEmpty(movie.Path))
        {
            return true;
        }

        if (record.IsFinished(movie.Id))
        {
            return true;
        }

        var stem = Stem(movie);
        var foreign = _mediaSourceManager.GetMediaStreams(movie.Id)
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
        var stem = Stem(movie);
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

        var wanted = perMovie - have.Count;
        if (wanted <= 0)
        {
            _logger.LogInformation("SubDL: {Movie} already has {Count} of our subtitle(s)", movie.Name, have.Count);
            return new MovieOutcome(0, Finished: true, QuotaSpent: false);
        }

        var fileName = Path.GetFileName(movie.Path);
        var results = await _subdl.SearchAsync(fileName, false, apiKey, cancellationToken).ConfigureAwait(false);
        if (results is null)
        {
            // The search call failed; leave the movie unfinished so the next run tries again.
            return new MovieOutcome(0, Finished: false, QuotaSpent: false);
        }

        var options = _libraryManager.GetLibraryOptions(movie);
        var folder = options.SaveSubtitlesWithMedia ? movie.ContainingFolderPath : movie.GetInternalMetadataPath();

        if (results.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(subSourceKey))
            {
                _logger.LogInformation("SubDL: no Persian subtitles for {Movie}", movie.Name);
                return new MovieOutcome(0, Finished: true, QuotaSpent: false);
            }

            var written = await FromSubSourceAsync(movie, stem, folder, have, wanted, subSourceKey, cancellationToken).ConfigureAwait(false);
            return new MovieOutcome(written, Finished: true, QuotaSpent: false);
        }

        var candidates = results
            .Where(r => !options.RequirePerfectSubtitleMatch || r.MatchScore >= PerfectMatchScore)
            .Select(r => new SubtitleCandidate(r.NId, r.ReleaseName ?? "SubDL", r.Author, r.MatchScore));
        var picks = SubtitlePicker.Pick(candidates, have, wanted);

        _logger.LogInformation(
            "SubDL: {Movie}: {Results} result(s), downloading {Picks} subtitle(s)",
            movie.Name,
            results.Count,
            picks.Count);

        var count = 0;
        foreach (var pick in picks)
        {
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
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "SubDL: download {Id} for {Movie} failed", pick.Id, movie.Name);
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
                continue;
            }

            _subdl.NoteDownloadUsed();

            if (await SaveAsync(response, movie, stem, folder, pick.Title, pick.Uploader ?? string.Empty, "SubDL", cancellationToken).ConfigureAwait(false))
            {
                count++;
            }
        }

        return new MovieOutcome(count, Finished: true, QuotaSpent: false);
    }

    private async Task<int> FromSubSourceAsync(
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

        var results = await _subSource.SearchAsync(imdb, movie.Name, movie.ProductionYear, false, null, null, apiKey, cancellationToken)
            .ConfigureAwait(false);

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
        foreach (var pick in picks)
        {
            HttpResponseMessage download;
            try
            {
                download = await _subSource.DownloadAsync(pick.Id, apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "SubSource: download {Id} for {Movie} failed", pick.Id, movie.Name);
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
                continue;
            }

            if (await SaveAsync(response, movie, stem, folder, pick.Title, pick.Uploader ?? SubSourceUploader, "SubSource", cancellationToken).ConfigureAwait(false))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Extracts the subtitle from the zip and writes it, never over an existing file.</summary>
    /// <returns>True when a file was written.</returns>
    private async Task<bool> SaveAsync(
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
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException)
        {
            _logger.LogWarning(ex, "{Source}: could not read the subtitle archive for {Movie}", source, movie.Name);
            return false;
        }

        var name = SubtitleNaming.BuildFileName(stem, title, uploader, subtitle.Format);
        var path = Path.Combine(folder, name);

        var created = false;
        await using (subtitle.Stream.ConfigureAwait(false))
        {
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
            }
            catch (IOException) when (!created && File.Exists(path))
            {
                _logger.LogDebug("SubDL: {File} already exists, skipping", name);
                return false;
            }
        }

        _logger.LogInformation("SubDL: saved {File} for {Movie}", name, movie.Name);
        return true;
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
}
