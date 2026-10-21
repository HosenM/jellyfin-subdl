using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubDL.Movies;

/// <summary>
/// Names movie subtitle files so that several Persian subtitles can sit beside one video,
/// one per uploader, and recognises them again later.
/// </summary>
internal static class SubtitleNaming
{
    /// <summary>Longest file name we create, kept well under common file system limits.</summary>
    public const int MaxFileNameLength = 200;

    private const string Separator = " - ";
    private const string FallbackTitle = "SubDL";
    private const string FallbackUploader = "Unknown";

    private static readonly string[] SubtitleExtensions = { "srt", "ass", "ssa", "vtt", "sub" };

    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").Distinct().ToArray();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Makes a title or uploader safe to embed in a file name. Dots become spaces so that no part
    /// is read by Jellyfin as a language or flag token (".en", ".forced", ".hi"), and the
    /// " - " separator is removed so that the uploader can be found again by splitting on it.
    /// </summary>
    /// <param name="value">The raw text.</param>
    /// <returns>The cleaned text, or an empty string.</returns>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '.')
            {
                builder.Append(' ');
            }
            else if (Array.IndexOf(InvalidChars, c) < 0)
            {
                builder.Append(c);
            }
        }

        var text = builder.ToString().Replace(Separator, " ", StringComparison.Ordinal);
        return Whitespace.Replace(text, " ").Trim();
    }

    /// <summary>
    /// Builds <c>{stem}.{title} - {uploader}.fa.{format}</c>, shortening only the title when the
    /// name would exceed <see cref="MaxFileNameLength"/>.
    /// </summary>
    /// <param name="videoStem">The video file name without its extension.</param>
    /// <param name="title">The release name from the subtitle source.</param>
    /// <param name="uploader">The uploader's name.</param>
    /// <param name="format">The subtitle format, such as srt.</param>
    /// <returns>The subtitle file name.</returns>
    public static string BuildFileName(string videoStem, string title, string uploader, string format)
    {
        var cleanTitle = Clean(title);
        var cleanUploader = Clean(uploader);
        if (cleanUploader.Length == 0)
        {
            cleanUploader = FallbackUploader;
        }

        var extension = format.ToLowerInvariant();
        var tail = Separator + cleanUploader + ".fa." + extension;
        var room = MaxFileNameLength - videoStem.Length - 1 - tail.Length;
        if (cleanTitle.Length > room)
        {
            cleanTitle = cleanTitle[..Math.Max(room, 0)].TrimEnd();
        }

        if (cleanTitle.Length == 0)
        {
            cleanTitle = FallbackTitle;
        }

        return videoStem + "." + cleanTitle + tail;
    }

    /// <summary>
    /// Tells whether a file is one of ours for this video and, if so, reads the uploader from it.
    /// </summary>
    /// <param name="videoStem">The video file name without its extension.</param>
    /// <param name="fileName">The subtitle file name.</param>
    /// <param name="uploader">The uploader found in the name.</param>
    /// <returns>True when the name has the per-uploader pattern.</returns>
    public static bool TryGetUploader(string videoStem, string fileName, out string uploader)
    {
        uploader = string.Empty;
        var prefix = videoStem + ".";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName).TrimStart('.');
        if (!SubtitleExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        const string persian = ".fa";
        var withoutExt = fileName[..^(ext.Length + 1)];
        if (withoutExt.Length < prefix.Length + persian.Length
            || !withoutExt.EndsWith(persian, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var middle = withoutExt[prefix.Length..^persian.Length];
        var at = middle.LastIndexOf(Separator, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        uploader = middle[(at + Separator.Length)..];
        return true;
    }
}
