using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.SubDL.Providers;

/// <summary>
/// Fetches Persian subtitles from subdl.com.
/// </summary>
public class SubdlSubtitleProvider : ISubtitleProvider
{
    private const string ApiBase = "https://api.subdl.com";

    // subdl documents >= 0.8 as a confident release match.
    private const double PerfectMatchScore = 0.8;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public string Name => "SubDL";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(request.MediaPath) || !IsPersian(request))
        {
            return [];
        }

        // subdl's "drop & match" endpoint takes the release filename and resolves both the
        // title and, for TV, the episode parsed out of the name.
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/api/v2/files/search?filename={1}&languages=fa&type={2}",
            ApiBase,
            Uri.EscapeDataString(Path.GetFileName(request.MediaPath)),
            request.ContentType == VideoContentType.Episode ? "tv" : "movie");

        using var response = await GetAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (payload?.Subtitles is null)
        {
            return [];
        }

        var language = string.IsNullOrWhiteSpace(request.Language) ? "fa" : request.Language;

        return payload.Subtitles
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

        var url = string.Format(CultureInfo.InvariantCulture, "{0}/api/v2/subtitles/{1}/download?format=zip", ApiBase, parts[0]);

        using var response = await GetAsync(url, apiKey, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var archive = new ZipArchive(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            ZipArchiveMode.Read);

        var entry = PickEntry(archive.Entries, ParseIndex(parts, 2), ParseIndex(parts, 3));

        var buffer = new MemoryStream();
        await using (var entryStream = entry.Open())
        {
            await entryStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        buffer.Position = 0;

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

    private static async Task<HttpResponseMessage> GetAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", "Bearer " + apiKey);
        return await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
