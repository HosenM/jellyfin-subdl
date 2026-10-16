using System.Globalization;
using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.SubDL.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL.Api;

/// <summary>
/// Reports the subdl account status behind the plugin config page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.SubtitleManagement)]
[Route("SubDL")]
public class SubdlController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubdlController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlController"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubdlController(IHttpClientFactory httpClientFactory, ILogger<SubdlController> logger)
    {
        _httpClientFactory = httpClientFactory;
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
            var client = _httpClientFactory.CreateClient(SubdlApi.HttpClientName);
            using var request = SubdlApi.CreateRequest(SubdlApi.BaseUrl + "/api/v2/me", apiKey);
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
            var me = await JsonSerializer.DeserializeAsync<SubdlApi.MeResponse>(stream, SubdlApi.JsonOptions, cancellationToken).ConfigureAwait(false);

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
