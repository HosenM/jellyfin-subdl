using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubDL;

/// <summary>
/// One Persian subtitle found on SubSource.
/// </summary>
/// <param name="Id">The subtitle id SubSource downloads by.</param>
/// <param name="ReleaseName">The release names SubSource lists for it, joined with commas.</param>
/// <param name="Uploader">Uploader of the subtitle, when SubSource says who it is.</param>
public sealed record SubSourceResult(string Id, string? ReleaseName, string? Uploader);

/// <summary>
/// Talks to the SubSource API. Shared by the subtitle provider and the scheduled movie task.
/// </summary>
public sealed class SubSourceClient
{
    private const string BaseUrl = "https://api.subsource.net/api/v1";

    // subsource names languages rather than using codes, in its own casing.
    private const string PersianLanguage = "farsi_persian";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubSourceClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSourceClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubSourceClient(IHttpClientFactory httpClientFactory, ILogger<SubSourceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Finds the title on SubSource and lists its Persian subtitles.
    /// </summary>
    /// <param name="imdbId">IMDb id of the movie; ignored for episodes.</param>
    /// <param name="title">Movie title, or the series name for an episode.</param>
    /// <param name="year">Production year, if known.</param>
    /// <param name="isEpisode">True for an episode.</param>
    /// <param name="season">Season number, if known.</param>
    /// <param name="episode">Episode number, if known.</param>
    /// <param name="apiKey">The SubSource API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The results; empty when there is no match or a call failed.</returns>
    public async Task<IReadOnlyList<SubSourceResult>> SearchAsync(
        string? imdbId,
        string? title,
        int? year,
        bool isEpisode,
        int? season,
        int? episode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var movieId = await FindMovieIdAsync(imdbId, title, year, isEpisode, season, apiKey, cancellationToken).ConfigureAwait(false);
        if (movieId is null)
        {
            _logger.LogInformation("SubSource: no title match for {Name}", title);
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
                title,
                (int)response.StatusCode);
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<SubtitleListResponse>(stream, PluginHttp.JsonOptions, cancellationToken).ConfigureAwait(false);

        if (payload?.Success == false || payload?.Data is null)
        {
            _logger.LogInformation("SubSource: no subtitles for {Name}", title);
            return [];
        }

        return payload.Data
            .Where(i => i.SubtitleId is not null)

            // Foreign-parts entries only cover the non-English dialogue, which is the wrong
            // thing to hand back when someone asks for a full Persian subtitle.
            .Where(i => i.ForeignParts != true)
            .Select(i => new SubSourceResult(
                i.SubtitleId!.Value.ToString(CultureInfo.InvariantCulture),
                i.ReleaseInfo is { Count: > 0 } ? string.Join(", ", i.ReleaseInfo) : null,
                null))
            .ToArray();
    }

    /// <summary>
    /// Requests the zip for a subtitle. The caller checks the status and disposes the response.
    /// </summary>
    /// <param name="id">The SubSource subtitle id.</param>
    /// <param name="apiKey">The SubSource API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response, headers read.</returns>
    public Task<HttpResponseMessage> DownloadAsync(string id, string apiKey, CancellationToken cancellationToken)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/subtitles/{1}/download?api_key={2}",
            BaseUrl,
            id,
            Uri.EscapeDataString(apiKey));

        return SendAsync(url, cancellationToken);
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

    private async Task<int?> FindMovieIdAsync(
        string? imdb,
        string? rawTitle,
        int? year,
        bool isEpisode,
        int? season,
        string apiKey,
        CancellationToken cancellationToken)
    {
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

        var title = rawTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var query = string.Format(
            CultureInfo.InvariantCulture,
            "searchType=text&q={0}",
            Uri.EscapeDataString(title.ToLowerInvariant()));

        if (isEpisode && season is not null)
        {
            query += string.Format(CultureInfo.InvariantCulture, "&season={0}", season);
        }

        var results = await SearchTitlesAsync(query, apiKey, cancellationToken).ConfigureAwait(false);

        // A text search can return a different film with a similar name, and the wrong film's
        // subtitles are worse than none, so require the title and year to line up.
        var wanted = title.ToLowerInvariant();
        return results.FirstOrDefault(r => Matches(r, wanted, year))?.MovieId;
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
        // subsource returns this as a JSON number, not a string.
        [JsonPropertyName("subtitleId")]
        public long? SubtitleId { get; set; }

        [JsonPropertyName("releaseInfo")]
        public List<string>? ReleaseInfo { get; set; }

        [JsonPropertyName("foreignParts")]
        public bool? ForeignParts { get; set; }
    }
}
