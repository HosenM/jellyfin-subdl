namespace Jellyfin.Plugin.SubDL.Movies;

/// <summary>A subtitle offered by the source, with how well it matches the video.</summary>
/// <param name="Id">The source's identifier for the subtitle.</param>
/// <param name="Title">The release name.</param>
/// <param name="Uploader">The uploader's name, if the source gave one.</param>
/// <param name="Score">The match score; higher is better.</param>
internal sealed record SubtitleCandidate(string Id, string Title, string? Uploader, double Score);

/// <summary>Chooses which subtitles to download: the best one from each uploader.</summary>
internal static class SubtitlePicker
{
    private const string UnknownUploader = "unknown";

    /// <summary>
    /// Makes a key that treats "Ali  Rezaei" and "ali rezaei" as the same uploader, and
    /// all uploaders without a name as one.
    /// </summary>
    /// <param name="uploader">The raw uploader name.</param>
    /// <returns>The lower-case cleaned name, or "unknown" when nothing is left.</returns>
    public static string UploaderKey(string? uploader)
    {
        var clean = SubtitleNaming.Clean(uploader);
        return clean.Length == 0 ? UnknownUploader : clean.ToLowerInvariant();
    }

    /// <summary>
    /// Picks the best-scoring subtitle of each uploader that is not already on disk,
    /// best uploaders first.
    /// </summary>
    /// <param name="candidates">Everything the source offered.</param>
    /// <param name="have">Uploader keys (see <see cref="UploaderKey"/>) already present beside the video.</param>
    /// <param name="count">The most subtitles to return.</param>
    /// <returns>At most <paramref name="count"/> candidates, one per uploader.</returns>
    public static IReadOnlyList<SubtitleCandidate> Pick(
        IEnumerable<SubtitleCandidate> candidates,
        IReadOnlySet<string> have,
        int count)
    {
        if (count <= 0)
        {
            return [];
        }

        return candidates
            .GroupBy(c => UploaderKey(c.Uploader))
            .Where(g => !have.Contains(g.Key))
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .Take(count)
            .ToList();
    }
}
