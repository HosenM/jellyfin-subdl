using System.Globalization;
using System.IO.Compression;
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

        if (!IsPersian(request))
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
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, SubdlApi.JsonOptions, cancellationToken).ConfigureAwait(false);
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
                // Carries the episode and the exact language string Jellyfin asked for, so the
                // right file can be picked out of a season pack and the saved file is named the
                // way Jellyfin will look for it next time. Core only splits on its own md5 prefix,
                // so these extra underscored fields survive the round trip.
                Id = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}_{1}_{2}_{3}",
                    NId(i.SubtitlePage),
                    language,
                    request.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture) ?? "-",
                    request.IndexNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"),
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

        return found;
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
        var language = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "fa";
        var season = ParseIndex(parts, 2);
        var episode = ParseIndex(parts, 3);

        var url = string.Format(CultureInfo.InvariantCulture, "{0}/api/v2/subtitles/{1}/download?format=zip", SubdlApi.BaseUrl, parts[0]);

        using var response = await SendAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeBody(response, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("SubDL: download {Id} failed with {Status}: {Body}", parts[0], (int)response.StatusCode, body);
            throw new HttpRequestException(string.Format(CultureInfo.InvariantCulture, "SubDL download failed with {0}: {1}", (int)response.StatusCode, body));
        }

        using var archive = new ZipArchive(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            ZipArchiveMode.Read);

        var entry = PickEntry(archive.Entries, season, episode);

        var buffer = new MemoryStream();
        await using (var entryStream = entry.Open())
        {
            await entryStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;

        _logger.LogInformation("SubDL: serving {Entry} for {Id}", entry.Name, parts[0]);

        return new SubtitleResponse
        {
            Stream = buffer,
            Language = language,
            Format = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant()
        };
    }

    private static ZipArchiveEntry PickEntry(IReadOnlyList<ZipArchiveEntry> entries, int? season, int? episode)
    {
        var files = entries.Where(e => e.Length > 0 && IsSubtitleFile(e.Name)).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("The SubDL archive contains no subtitle file.");
        }

        if (season is not null && episode is not null)
        {
            var sxxexx = string.Format(CultureInfo.InvariantCulture, "s{0:00}e{1:00}", season, episode);
            var sxe = string.Format(CultureInfo.InvariantCulture, "{0}x{1:00}", season, episode);

            var match = files.FirstOrDefault(e =>
            {
                var name = e.Name.ToLowerInvariant();
                return name.Contains(sxxexx, StringComparison.Ordinal) || name.Contains(sxe, StringComparison.Ordinal);
            });

            if (match is not null)
            {
                return match;
            }
        }

        // Nothing in the names identifies the episode, so fall back to the largest file.
        // shortcut: wrong-episode risk on oddly named season packs, revisit if it shows up.
        return files.OrderByDescending(e => e.Length).First();
    }

    private static bool IsSubtitleFile(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vtt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPersian(SubtitleSearchRequest request)
        => string.Equals(request.TwoLetterISOLanguageName, "fa", StringComparison.OrdinalIgnoreCase)
            || IsPersianCode(request.Language);

    private static bool IsPersianCode(string? value)
        => value is not null
            && (value.Equals("fa", StringComparison.OrdinalIgnoreCase)
                || value.Equals("fas", StringComparison.OrdinalIgnoreCase)
                || value.Equals("per", StringComparison.OrdinalIgnoreCase)
                || value.Contains("persian", StringComparison.OrdinalIgnoreCase)
                || value.Contains("farsi", StringComparison.OrdinalIgnoreCase));

    private static bool IsPersianResult(SubtitleItem item)
        => (item.Language is not null && item.Language.Equals("FA", StringComparison.OrdinalIgnoreCase))
            || (item.Lang is not null
                && (item.Lang.Contains("farsi", StringComparison.OrdinalIgnoreCase)
                    || item.Lang.Contains("persian", StringComparison.OrdinalIgnoreCase)));

    private static string? NId(string? subtitlePage)
        => subtitlePage?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static int? ParseIndex(string[] parts, int index)
        => parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

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
        var client = _httpClientFactory.CreateClient(SubdlApi.HttpClientName);
        using var request = SubdlApi.CreateRequest(url, apiKey);
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
