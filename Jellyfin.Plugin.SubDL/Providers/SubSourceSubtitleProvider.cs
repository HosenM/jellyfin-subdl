using System.Globalization;
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
    private readonly SubSourceClient _client;
    private readonly ILogger<SubSourceSubtitleProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSourceSubtitleProvider"/> class.
    /// </summary>
    /// <param name="client">The shared SubSource client.</param>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    public SubSourceSubtitleProvider(SubSourceClient client, ILogger<SubSourceSubtitleProvider> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "SubSource";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (SubtitleHelpers.IsLeftToMovieTask(request.ContentType, Plugin.Instance?.Configuration.SubtitlesPerMovie ?? 0))
        {
            _logger.LogDebug("SubSource: leaving movie {Name} to the movie subtitle task", request.Name);
            return [];
        }

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

        var results = await _client.SearchAsync(
            GetProviderId(request, "Imdb"),
            isEpisode ? request.SeriesName : request.Name,
            request.ProductionYear,
            isEpisode,
            season,
            episode,
            apiKey,
            cancellationToken).ConfigureAwait(false);

        var language = string.IsNullOrWhiteSpace(request.Language) ? "fa" : request.Language;

        // A failed call and no match look the same to Jellyfin: nothing from this provider.
        var found = (results ?? [])
            .Select(i => new RemoteSubtitleInfo
            {
                Id = SubtitleHelpers.BuildId(i.Id, language, season, episode),
                ProviderName = Name,
                Name = i.ReleaseName ?? "SubSource",
                Author = i.Uploader,
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

        using var response = await _client.DownloadAsync(parts[0], apiKey, cancellationToken).ConfigureAwait(false);
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
}
