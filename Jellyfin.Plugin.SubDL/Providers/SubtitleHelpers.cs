using System.Globalization;
using System.IO.Compression;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;

namespace Jellyfin.Plugin.SubDL.Providers;

/// <summary>
/// Helpers shared by the subtitle providers.
/// </summary>
internal static class SubtitleHelpers
{
    /// <summary>
    /// Formats the identifier handed back to Jellyfin. It carries the episode and the exact
    /// language string Jellyfin asked for, so the right file can be picked out of a season
    /// pack and the saved file is named the way Jellyfin will look for it next time. Core only
    /// splits on its own md5 prefix, so these extra underscored fields survive the round trip.
    /// </summary>
    /// <param name="id">Provider side identifier.</param>
    /// <param name="language">Requested language.</param>
    /// <param name="season">Season number, if known.</param>
    /// <param name="episode">Episode number, if known.</param>
    /// <returns>The composite identifier.</returns>
    public static string BuildId(string id, string language, int? season, int? episode)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}_{1}_{2}_{3}",
            id,
            language,
            season?.ToString(CultureInfo.InvariantCulture) ?? "-",
            episode?.ToString(CultureInfo.InvariantCulture) ?? "-");

    /// <summary>
    /// Parses one of the numeric fields of a composite identifier.
    /// </summary>
    /// <param name="parts">Identifier split on underscores.</param>
    /// <param name="index">Field index.</param>
    /// <returns>The number, or null.</returns>
    public static int? ParseIndex(string[] parts, int index)
        => parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// Parses the language field of a composite identifier.
    /// </summary>
    /// <param name="parts">Identifier split on underscores.</param>
    /// <returns>The language string.</returns>
    public static string ParseLanguage(string[] parts)
        => parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "fa";

    /// <summary>
    /// Picks a subtitle out of a provider archive and reads it into a response.
    /// </summary>
    /// <param name="archiveStream">The zip stream.</param>
    /// <param name="language">Language to report back.</param>
    /// <param name="season">Season number, if known.</param>
    /// <param name="episode">Episode number, if known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The subtitle response.</returns>
    public static async Task<SubtitleResponse> ExtractAsync(
        Stream archiveStream,
        string language,
        int? season,
        int? episode,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var entry = PickEntry(archive.Entries, season, episode);

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

    /// <summary>
    /// Checks whether a request is for Persian. Only Persian is supported.
    /// </summary>
    /// <param name="request">The search request.</param>
    /// <returns>True when Persian was asked for.</returns>
    public static bool IsPersian(SubtitleSearchRequest request)
        => string.Equals(request.TwoLetterISOLanguageName, "fa", StringComparison.OrdinalIgnoreCase)
            || IsPersianCode(request.Language);

    /// <summary>
    /// Checks whether a language code or name means Persian.
    /// </summary>
    /// <param name="value">Language code or name.</param>
    /// <returns>True when Persian.</returns>
    public static bool IsPersianCode(string? value)
        => value is not null
            && (value.Equals("fa", StringComparison.OrdinalIgnoreCase)
                || value.Equals("fas", StringComparison.OrdinalIgnoreCase)
                || value.Equals("per", StringComparison.OrdinalIgnoreCase)
                || value.Contains("persian", StringComparison.OrdinalIgnoreCase)
                || value.Contains("farsi", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks whether a search should be left to the scheduled movie task. When the movie
    /// task is on, movies get several subtitles from it, so the providers stay out of the way.
    /// </summary>
    /// <param name="contentType">Kind of video searched for.</param>
    /// <param name="subtitlesPerMovie">The configured number of subtitles per movie, 0 for off.</param>
    /// <returns>True when the movie task handles this search.</returns>
    public static bool IsLeftToMovieTask(VideoContentType contentType, int subtitlesPerMovie)
        => contentType == VideoContentType.Movie && subtitlesPerMovie > 0;

    private static ZipArchiveEntry PickEntry(IReadOnlyList<ZipArchiveEntry> entries, int? season, int? episode)
    {
        var files = entries.Where(e => e.Length > 0 && IsSubtitleFile(e.Name)).ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException("The subtitle archive contains no subtitle file.");
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
}
