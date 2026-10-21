using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL;

/// <summary>
/// One Persian subtitle found on subdl.
/// </summary>
/// <param name="NId">The subtitle id subdl downloads by.</param>
/// <param name="ReleaseName">Release the subtitle was made for.</param>
/// <param name="Author">Uploader of the subtitle.</param>
/// <param name="MatchScore">How well subdl thinks the subtitle fits the searched file.</param>
public sealed record SubdlResult(string NId, string? ReleaseName, string? Author, double MatchScore);

/// <summary>
/// Talks to the subdl API. Shared by the subtitle provider and the scheduled movie task.
/// </summary>
public sealed class SubdlClient
{
    // The free tier allows 50 downloads a day, and once that is gone every download fails.
    // Offering results the plugin cannot fetch would stop Jellyfin from moving on to the next
    // provider, so the remaining downloads are checked before anything is returned.
    private static readonly TimeSpan QuotaCacheDuration = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubdlClient> _logger;

    private DateTime _downloadsCheckedAt = DateTime.MinValue;
    private int _downloadsRemaining = int.MaxValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubdlClient(IHttpClientFactory httpClientFactory, ILogger<SubdlClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Searches subdl for Persian subtitles for a release file name.
    /// </summary>
    /// <param name="fileName">Release file name.</param>
    /// <param name="isEpisode">True to search TV, false for movies.</param>
    /// <param name="apiKey">The subdl API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results with the best match first, or null when the search failed.</returns>
    public async Task<IReadOnlyList<SubdlResult>?> SearchAsync(
        string fileName,
        bool isEpisode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        // subdl's "drop & match" endpoint takes the release filename and resolves both the
        // title and, for TV, the episode parsed out of the name.
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/api/v2/files/search?filename={1}&languages=fa&type={2}",
            SubdlApi.BaseUrl,
            Uri.EscapeDataString(fileName),
            isEpisode ? "tv" : "movie");

        using var response = await SendAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "SubDL: search for {File} failed with {Status}: {Body}",
                fileName,
                (int)response.StatusCode,
                await PluginHttp.SafeBodyAsync(response, cancellationToken).ConfigureAwait(false));
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload?.Subtitles is null)
        {
            _logger.LogInformation("SubDL: {File} returned no subtitles", fileName);
            return [];
        }

        return payload.Subtitles
            .Where(i => NId(i.SubtitlePage) is not null && i.MatchScore is not null && IsPersianResult(i))
            .OrderByDescending(i => i.MatchScore)
            .Select(i => new SubdlResult(NId(i.SubtitlePage)!, i.ReleaseName, i.Author, i.MatchScore!.Value))
            .ToArray();
    }

    /// <summary>
    /// Checks whether subdl still has downloads left today. A failure to read the quota is not
    /// treated as exhaustion, so a hiccup on /me cannot silently disable the provider.
    /// </summary>
    /// <param name="apiKey">The subdl API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when downloads remain, or when the quota could not be read.</returns>
    public async Task<bool> HasDownloadQuotaAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _downloadsCheckedAt < QuotaCacheDuration)
        {
            return Volatile.Read(ref _downloadsRemaining) > 0;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(PluginHttp.ClientName);
            using var request = PluginHttp.CreateBearerRequest(SubdlApi.BaseUrl + "/api/v2/me", apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return true;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var me = await JsonSerializer.DeserializeAsync<SubdlApi.MeResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);

            var downloads = me?.Usage?.Downloads;
            if (downloads is null)
            {
                return true;
            }

            var remaining = downloads.Remaining ?? (downloads.Limit - downloads.Used);
            Volatile.Write(ref _downloadsRemaining, remaining);
            _downloadsCheckedAt = DateTime.UtcNow;

            return remaining > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "SubDL: could not read the download quota");
            return true;
        }
    }

    /// <summary>
    /// Records that a download was spent, so several downloads inside one cached minute
    /// cannot overshoot the quota.
    /// </summary>
    public void NoteDownloadUsed()
    {
        if (Volatile.Read(ref _downloadsRemaining) > 0)
        {
            Interlocked.Decrement(ref _downloadsRemaining);
        }
    }

    /// <summary>
    /// Forgets the cached quota so the next <see cref="HasDownloadQuotaAsync"/> asks subdl again.
    /// Used after a refused download, which may mean the quota ran out sooner than the cache knows.
    /// </summary>
    public void ExpireQuotaCache()
    {
        _downloadsCheckedAt = DateTime.MinValue;
    }

    /// <summary>
    /// Requests the zip for a subtitle. The caller checks the status and disposes the response.
    /// </summary>
    /// <param name="nId">The subdl subtitle id.</param>
    /// <param name="apiKey">The subdl API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response, headers read.</returns>
    public Task<HttpResponseMessage> DownloadAsync(string nId, string apiKey, CancellationToken cancellationToken)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/api/v2/subtitles/{1}/download?format=zip",
            SubdlApi.BaseUrl,
            nId);

        return SendAsync(url, apiKey, cancellationToken);
    }

    private static bool IsPersianResult(SubtitleItem item)
        => (item.Language is not null && item.Language.Equals("FA", StringComparison.OrdinalIgnoreCase))
            || (item.Lang is not null
                && (item.Lang.Contains("farsi", StringComparison.OrdinalIgnoreCase)
                    || item.Lang.Contains("persian", StringComparison.OrdinalIgnoreCase)));

    private static string? NId(string? subtitlePage)
        => subtitlePage?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private async Task<HttpResponseMessage> SendAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(PluginHttp.ClientName);
        using var request = PluginHttp.CreateBearerRequest(url, apiKey);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private sealed class SearchResponse
    {
        [JsonPropertyName("subtitles")]
        public List<SubtitleItem>? Subtitles { get; set; }
    }

    private sealed class SubtitleItem
    {
        [JsonPropertyName("release_name")]
        public string? ReleaseName { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("lang")]
        public string? Lang { get; set; }

        [JsonPropertyName("subtitlePage")]
        public string? SubtitlePage { get; set; }

        [JsonPropertyName("match_score")]
        public double? MatchScore { get; set; }
    }
}
