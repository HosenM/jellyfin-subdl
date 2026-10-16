using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.SubDL.Archive;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL.Api;

/// <summary>
/// Reports the subdl account status and archives orphaned subtitles.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.SubtitleManagement)]
[Route("SubDL")]
public class SubdlController : ControllerBase
{
    private const int MaxReportedItems = 200;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SubdlController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubdlController(
        IHttpClientFactory httpClientFactory,
        ILibraryManager libraryManager,
        ILogger<SubdlController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Returns the plan and the remaining daily quota for the configured key.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    public async Task<ActionResult<StatusResult>> GetStatus(CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new StatusResult { Configured = false, Error = "No API key configured." };
        }

        try
        {
            var client = _httpClientFactory.CreateClient(PluginHttp.ClientName);
            using var request = PluginHttp.CreateBearerRequest(SubdlApi.BaseUrl + "/api/v2/me", apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new StatusResult
                {
                    Configured = true,
                    Error = string.Format(CultureInfo.InvariantCulture, "subdl returned {0} ({1}).", (int)response.StatusCode, response.StatusCode)
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var me = await JsonSerializer.DeserializeAsync<SubdlApi.MeResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);

            return new StatusResult
            {
                Configured = true,
                Plan = me?.Plan?.Name,
                SearchesUsed = me?.Usage?.Search?.Used ?? 0,
                SearchesLimit = me?.Usage?.Search?.Limit ?? 0,
                DownloadsUsed = me?.Usage?.Downloads?.Used ?? 0,
                DownloadsLimit = me?.Usage?.Downloads?.Limit ?? 0,
                ResetAt = me?.Usage?.Downloads?.ResetAt
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "SubDL: could not read account status");
            return new StatusResult { Configured = true, Error = ex.Message };
        }
    }

    /// <summary>
    /// Lists subtitle files in the libraries whose video file is gone.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The orphans.</returns>
    [HttpGet("Orphans")]
    public async Task<ActionResult<OrphanReport>> GetOrphans(CancellationToken cancellationToken)
    {
        var archivePath = Plugin.Instance?.Configuration.ArchivePath;
        var locations = GetLibraryLocations();

        IReadOnlyList<string> orphans;
        try
        {
            orphans = await Task.Run(() => OrphanArchiver.FindOrphans(locations), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubDL: scanning for orphaned subtitles failed");
            return new OrphanReport { ArchivePath = archivePath, Error = ex.Message };
        }

        return new OrphanReport
        {
            ArchivePath = archivePath,
            Locations = locations.ToArray(),
            Count = orphans.Count,
            Orphans = orphans.Take(MaxReportedItems).ToArray()
        };
    }

    /// <summary>
    /// Moves the orphaned subtitles into the configured archive folder.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was moved.</returns>
    [HttpPost("Orphans/Archive")]
    public async Task<ActionResult<ArchiveReport>> ArchiveOrphans(CancellationToken cancellationToken)
    {
        var archivePath = Plugin.Instance?.Configuration.ArchivePath;
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return new ArchiveReport { Error = "No archive path configured." };
        }

        var locations = GetLibraryLocations();

        // Re-scan rather than trusting a list from the client, so a stale or hostile request
        // cannot point the move at an arbitrary file.
        IReadOnlyList<string> orphans;
        try
        {
            orphans = await Task.Run(() => OrphanArchiver.FindOrphans(locations), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubDL: scanning for orphaned subtitles failed");
            return new ArchiveReport { ArchivePath = archivePath, Error = ex.Message };
        }

        try
        {
            var outcome = await Task.Run(() => OrphanArchiver.Archive(orphans, archivePath, cancellationToken), cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "SubDL: archived {Moved} orphaned subtitle(s) to {Path}, {Failed} failure(s)",
                outcome.Moved,
                archivePath,
                outcome.Errors.Count);

            return new ArchiveReport
            {
                ArchivePath = archivePath,
                Moved = outcome.Moved,
                Failed = outcome.Errors.Count,
                Errors = outcome.Errors.Take(MaxReportedItems).ToArray()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _logger.LogError(ex, "SubDL: could not write to the archive path {Path}", archivePath);
            return new ArchiveReport { ArchivePath = archivePath, Error = ex.Message };
        }
    }

    private List<string> GetLibraryLocations()
        => _libraryManager.GetVirtualFolders()
            .SelectMany(folder => folder.Locations ?? [])
            .ToList();

    /// <summary>
    /// Result of an orphan scan.
    /// </summary>
    public sealed class OrphanReport
    {
        /// <summary>
        /// Gets or sets the configured archive path.
        /// </summary>
        public string? ArchivePath { get; set; }

        /// <summary>
        /// Gets or sets the library folders that were scanned.
        /// </summary>
        public string[] Locations { get; set; } = [];

        /// <summary>
        /// Gets or sets how many orphans were found.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Gets or sets the orphans, capped for the response.
        /// </summary>
        public string[] Orphans { get; set; } = [];

        /// <summary>
        /// Gets or sets the error, if any.
        /// </summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Result of an archive run.
    /// </summary>
    public sealed class ArchiveReport
    {
        /// <summary>
        /// Gets or sets the archive path used.
        /// </summary>
        public string? ArchivePath { get; set; }

        /// <summary>
        /// Gets or sets how many files were moved.
        /// </summary>
        public int Moved { get; set; }

        /// <summary>
        /// Gets or sets how many files could not be moved.
        /// </summary>
        public int Failed { get; set; }

        /// <summary>
        /// Gets or sets per file failures, capped for the response.
        /// </summary>
        public string[] Errors { get; set; } = [];

        /// <summary>
        /// Gets or sets the error, if any.
        /// </summary>
        public string? Error { get; set; }
    }

    /// <summary>
    /// Status of the configured subdl account.
    /// </summary>
    public sealed class StatusResult
    {
        /// <summary>
        /// Gets or sets a value indicating whether an API key is set.
        /// </summary>
        public bool Configured { get; set; }

        /// <summary>
        /// Gets or sets the plan name.
        /// </summary>
        public string? Plan { get; set; }

        /// <summary>
        /// Gets or sets the searches used today.
        /// </summary>
        public int SearchesUsed { get; set; }

        /// <summary>
        /// Gets or sets the daily search limit.
        /// </summary>
        public int SearchesLimit { get; set; }

        /// <summary>
        /// Gets or sets the downloads used today.
        /// </summary>
        public int DownloadsUsed { get; set; }

        /// <summary>
        /// Gets or sets the daily download limit.
        /// </summary>
        public int DownloadsLimit { get; set; }

        /// <summary>
        /// Gets or sets when the daily quota resets.
        /// </summary>
        public string? ResetAt { get; set; }

        /// <summary>
        /// Gets or sets the error, if any.
        /// </summary>
        public string? Error { get; set; }
    }
}
