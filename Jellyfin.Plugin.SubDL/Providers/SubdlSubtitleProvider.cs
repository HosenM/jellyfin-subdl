using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL.Providers;

/// <summary>
/// Fetches Persian subtitles from subdl.com.
/// </summary>
public class SubdlSubtitleProvider : ISubtitleProvider
{
    // subdl documents >= 0.8 as a confident release match.
    private const double PerfectMatchScore = 0.8;

    // The free tier allows 50 downloads a day, and once that is gone every download fails.
    // Offering results the plugin cannot fetch would stop Jellyfin from moving on to the next
    // provider, so the remaining downloads are checked before anything is returned.
    private static readonly TimeSpan QuotaCacheDuration = TimeSpan.FromMinutes(1);

    private static DateTime _downloadsCheckedAt = DateTime.MinValue;
    private static int _downloadsRemaining = int.MaxValue;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubdlSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlSubtitleProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubdlSubtitleProvider(IHttpClientFactory httpClientFactory, ILogger<SubdlSubtitleProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubDL";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("SubDL: no API key configured, skipping search for {Name}", request.Name);
            return [];
        }

        if (string.IsNullOrWhiteSpace(request.MediaPath))
        {
            _logger.LogWarning("SubDL: {Name} has no media path, skipping search", request.Name);
            return [];
        }

        if (!SubtitleHelpers.IsPersian(request))
        {
            return [];
        }

        var fileName = Path.GetFileName(request.MediaPath);

        // subdl's "drop & match" endpoint takes the release filename and resolves both the
        // title and, for TV, the episode parsed out of the name.
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/api/v2/files/search?filename={1}&languages=fa&type={2}",
            SubdlApi.BaseUrl,
            Uri.EscapeDataString(fileName),
            request.ContentType == VideoContentType.Episode ? "tv" : "movie");

        using var response = await SendAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "SubDL: search for {File} failed with {Status}: {Body}",
                fileName,
                (int)response.StatusCode,
                await SafeBody(response, cancellationToken).ConfigureAwait(false));
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload?.Subtitles is null)
        {
            _logger.LogInformation("SubDL: {File} returned no subtitles", fileName);
            return [];
        }

        var language = string.IsNullOrWhiteSpace(request.Language) ? "fa" : request.Language;

        var found = payload.Subtitles
            .Where(i => NId(i.SubtitlePage) is not null && i.MatchScore is not null && IsPersianResult(i))
            .Where(i => !request.IsPerfectMatch || i.MatchScore >= PerfectMatchScore)
            .OrderByDescending(i => i.MatchScore)
            .Select(i => new RemoteSubtitleInfo
            {
                Id = SubtitleHelpers.BuildId(NId(i.SubtitlePage)!, language, request.ParentIndexNumber, request.IndexNumber),
                ProviderName = Name,
                Name = i.ReleaseName ?? "SubDL",
                Author = i.Author,
                Format = "srt",
                ThreeLetterISOLanguageName = "fas"
            })
            .ToArray();

        _logger.LogInformation(
            "SubDL: {Count} Persian subtitle(s) for {File}{Perfect}",
            found.Length,
            fileName,
            request.IsPerfectMatch ? " (perfect match required)" : string.Empty);

        if (found.Length > 0 && !await HasDownloadQuotaAsync(apiKey, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "SubDL: daily download quota is spent, handing {Count} result(s) over to the next provider",
                found.Length);
            return [];
        }

        return found;
    }

    /// <summary>
    /// Checks whether subdl still has downloads left today. A failure to read the quota is not
    /// treated as exhaustion, so a hiccup on /me cannot silently disable the provider.
    /// </summary>
    private async Task<bool> HasDownloadQuotaAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _downloadsCheckedAt < QuotaCacheDuration)
        {
            return _downloadsRemaining > 0;
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

            _downloadsRemaining = downloads.Remaining ?? (downloads.Limit - downloads.Used);
            _downloadsCheckedAt = DateTime.UtcNow;

            return _downloadsRemaining > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "SubDL: could not read the download quota");
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The SubDL API key is not configured.");
        }

        var parts = id.Split('_', 4);
        var language = SubtitleHelpers.ParseLanguage(parts);
        var season = SubtitleHelpers.ParseIndex(parts, 2);
        var episode = SubtitleHelpers.ParseIndex(parts, 3);

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/api/v2/subtitles/{1}/download?format=zip",
            SubdlApi.BaseUrl,
            parts[0]);

        using var response = await SendAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeBody(response, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("SubDL: download {Id} failed with {Status}: {Body}", parts[0], (int)response.StatusCode, body);
            throw new HttpRequestException(string.Format(
                CultureInfo.InvariantCulture,
                "SubDL download failed with {0}: {1}",
                (int)response.StatusCode,
                body));
        }

        var result = await SubtitleHelpers.ExtractAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            language,
            season,
            episode,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("SubDL: serving subtitle {Id}", parts[0]);

        return result;
    }

    private static bool IsPersianResult(SubtitleItem item)
        => (item.Language is not null && item.Language.Equals("FA", StringComparison.OrdinalIgnoreCase))
            || (item.Lang is not null
                && (item.Lang.Contains("farsi", StringComparison.OrdinalIgnoreCase)
                    || item.Lang.Contains("persian", StringComparison.OrdinalIgnoreCase)));

    private static string? NId(string? subtitlePage)
        => subtitlePage?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static async Task<string> SafeBody(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception)
        {
            return "<unreadable>";
        }
    }

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
