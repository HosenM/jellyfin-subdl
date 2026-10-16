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
/// Fetches Persian subtitles from subsource.net, used as a second source behind subdl.
/// </summary>
public class SubSourceSubtitleProvider : ISubtitleProvider
{
    private const string BaseUrl = "https://api.subsource.net/api/v1";

    // subsource names languages rather than using codes, in its own casing.
    private const string PersianLanguage = "farsi_persian";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubSourceSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSourceSubtitleProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubSourceSubtitleProvider(IHttpClientFactory httpClientFactory, ILogger<SubSourceSubtitleProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubSource";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.SubSourceApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        if (!SubtitleHelpers.IsPersian(request))
        {
            return [];
        }

        var isEpisode = request.ContentType == VideoContentType.Episode;
        var season = request.ParentIndexNumber;
        var episode = request.IndexNumber;

        var movieId = await FindMovieIdAsync(request, isEpisode, apiKey, cancellationToken).ConfigureAwait(false);
        if (movieId is null)
        {
            _logger.LogInformation("SubSource: no title match for {Name}", request.Name);
            return [];
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/subtitles?api_key={1}&movieId={2}&language={3}&limit=100",
            BaseUrl,
            Uri.EscapeDataString(apiKey),
            movieId,
            PersianLanguage);

        if (isEpisode)
        {
            if (season is not null)
            {
                url += string.Format(CultureInfo.InvariantCulture, "&seasonNumber={0}", season);
            }

            if (episode is not null)
            {
                url += string.Format(CultureInfo.InvariantCulture, "&episodeNumber={0}", episode);
            }
        }

        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "SubSource: subtitle list for {Name} failed with {Status}",
                request.Name,
                (int)response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<SubtitleListResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);

        if (payload?.Success == false || payload?.Data is null)
        {
            _logger.LogInformation("SubSource: no subtitles for {Name}", request.Name);
            return [];
        }

        var language = string.IsNullOrWhiteSpace(request.Language) ? "fa" : request.Language;

        var found = payload.Data
            .Where(i => !string.IsNullOrWhiteSpace(i.SubtitleId))

            // Foreign-parts entries only cover the non-English dialogue, which is the wrong
            // thing to hand back when someone asks for a full Persian subtitle.
            .Where(i => i.ForeignParts != true)
            .Select(i => new RemoteSubtitleInfo
            {
                Id = SubtitleHelpers.BuildId(i.SubtitleId!, language, season, episode),
                ProviderName = Name,
                Name = i.ReleaseInfo is { Count: > 0 } ? string.Join(", ", i.ReleaseInfo) : "SubSource",
                Format = "srt",
                ThreeLetterISOLanguageName = "fas"
            })
            .ToArray();

        _logger.LogInformation("SubSource: {Count} Persian subtitle(s) for {Name}", found.Length, request.Name);

        return found;
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.SubSourceApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("The SubSource API key is not configured.");
        }

        var parts = id.Split('_', 4);
        var language = SubtitleHelpers.ParseLanguage(parts);
        var season = SubtitleHelpers.ParseIndex(parts, 2);
        var episode = SubtitleHelpers.ParseIndex(parts, 3);

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/subtitles/{1}/download?api_key={2}",
            BaseUrl,
            parts[0],
            Uri.EscapeDataString(apiKey));

        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SubSource: download {Id} failed with {Status}", parts[0], (int)response.StatusCode);
            throw new HttpRequestException(string.Format(
                CultureInfo.InvariantCulture,
                "SubSource download failed with {0}.",
                (int)response.StatusCode));
        }

        _logger.LogInformation("SubSource: serving subtitle {Id}", parts[0]);

        return await SubtitleHelpers.ExtractAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            language,
            season,
            episode,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> FindMovieIdAsync(
        SubtitleSearchRequest request,
        bool isEpisode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var imdb = GetProviderId(request, "Imdb");

        // For an episode the provider id is the episode's own, and subsource wants the series',
        // so episodes always go through the text search with the series name.
        if (!isEpisode && !string.IsNullOrWhiteSpace(imdb))
        {
            var byImdb = await SearchTitlesAsync(
                string.Format(CultureInfo.InvariantCulture, "searchType=imdb&imdb={0}", Uri.EscapeDataString(imdb)),
                apiKey,
                cancellationToken).ConfigureAwait(false);

            var exact = byImdb.FirstOrDefault();
            if (exact?.MovieId is not null)
            {
                return exact.MovieId;
            }
        }

        var title = (isEpisode ? request.SeriesName : request.Name)?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = string.Format(
            CultureInfo.InvariantCulture,
            "searchType=text&q={0}",
            Uri.EscapeDataString(title.ToLowerInvariant()));

        if (isEpisode && request.ParentIndexNumber is not null)
        {
            query += string.Format(CultureInfo.InvariantCulture, "&season={0}", request.ParentIndexNumber);
        }

        var results = await SearchTitlesAsync(query, apiKey, cancellationToken).ConfigureAwait(false);

        // A text search can return a different film with a similar name, and the wrong film's
        // subtitles are worse than none, so require the title and year to line up.
        var wanted = title.ToLowerInvariant();
        return results.FirstOrDefault(r => Matches(r, wanted, request.ProductionYear))?.MovieId;
    }

    private static bool Matches(TitleItem item, string wanted, int? year)
    {
        if (year is not null && item.ReleaseYear is not null && item.ReleaseYear != year)
        {
            return false;
        }

        var title = item.Title?.ToLowerInvariant();
        var alternate = item.AlternateTitle?.ToLowerInvariant();

        return (title is not null && title.Contains(wanted, StringComparison.Ordinal))
            || (alternate is not null && alternate.Contains(wanted, StringComparison.Ordinal));
    }

    private async Task<List<TitleItem>> SearchTitlesAsync(string query, string apiKey, CancellationToken cancellationToken)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/movies/search?api_key={1}&{2}",
            BaseUrl,
            Uri.EscapeDataString(apiKey),
            query);

        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SubSource: title search failed with {Status}", (int)response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var payload = await JsonSerializer.DeserializeAsync<TitleSearchResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);
            return payload?.Data ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "SubSource: could not read the title search response");
            return [];
        }
    }

    private static string? GetProviderId(SubtitleSearchRequest request, string key)
    {
        foreach (var pair in request.ProviderIds)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(PluginHttp.ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private sealed class TitleSearchResponse
    {
        [JsonPropertyName("data")]
        public List<TitleItem>? Data { get; set; }
    }

    private sealed class TitleItem
    {
        [JsonPropertyName("movieId")]
        public int? MovieId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("alternateTitle")]
        public string? AlternateTitle { get; set; }

        [JsonPropertyName("releaseYear")]
        public int? ReleaseYear { get; set; }
    }

    private sealed class SubtitleListResponse
    {
        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        [JsonPropertyName("data")]
        public List<SubtitleItem>? Data { get; set; }
    }

    private sealed class SubtitleItem
    {
        [JsonPropertyName("subtitleId")]
        public string? SubtitleId { get; set; }

        [JsonPropertyName("releaseInfo")]
        public List<string>? ReleaseInfo { get; set; }

        [JsonPropertyName("foreignParts")]
        public bool? ForeignParts { get; set; }
    }
}
