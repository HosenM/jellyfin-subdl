using System.Globalization;
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

    private readonly SubdlClient _client;
    private readonly ILogger<SubdlSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubdlSubtitleProvider"/> class.
    /// </summary>
    /// <param name="client">The shared subdl client.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubdlSubtitleProvider(SubdlClient client, ILogger<SubdlSubtitleProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubDL";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (SubtitleHelpers.IsLeftToMovieTask(request.ContentType, Plugin.Instance?.Configuration.SubtitlesPerMovie ?? 0))
        {
            _logger.LogDebug("SubDL: leaving movie {Name} to the movie subtitle task", request.Name);
            return [];
        }

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

        var results = await _client.SearchAsync(
            fileName,
            request.ContentType == VideoContentType.Episode,
            apiKey,
            cancellationToken).ConfigureAwait(false);
        if (results is null)
        {
            return [];
        }

        var language = string.IsNullOrWhiteSpace(request.Language) ? "fa" : request.Language;

        var found = results
            .Where(i => !request.IsPerfectMatch || i.MatchScore >= PerfectMatchScore)
            .Select(i => new RemoteSubtitleInfo
            {
                Id = SubtitleHelpers.BuildId(i.NId, language, request.ParentIndexNumber, request.IndexNumber),
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

        if (found.Length > 0 && !await _client.HasDownloadQuotaAsync(apiKey, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning(
                "SubDL: daily download quota is spent, handing {Count} result(s) over to the next provider",
                found.Length);
            return [];
        }

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
        var language = SubtitleHelpers.ParseLanguage(parts);
        var season = SubtitleHelpers.ParseIndex(parts, 2);
        var episode = SubtitleHelpers.ParseIndex(parts, 3);

        using var response = await _client.DownloadAsync(parts[0], apiKey, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await PluginHttp.SafeBodyAsync(response, cancellationToken).ConfigureAwait(false);
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
}
