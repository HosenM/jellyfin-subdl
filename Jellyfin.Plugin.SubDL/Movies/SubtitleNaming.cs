using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubDL.Movies;

/// <summary>
/// Names movie subtitle files so that several Persian subtitles can sit beside one video,
/// one per uploader, and recognises them again later.
/// </summary>
internal static class SubtitleNaming
{
    /// <summary>Longest file name we create, in UTF-8 bytes (Linux limits names to 255 bytes and Persian is 2 bytes per letter).</summary>
    public const int MaxFileNameLength = 200;

    /// <summary>Longest file name most file systems accept, in UTF-8 bytes (ext4, XFS, Btrfs and NTFS all stop near 255).</summary>
    public const int FileSystemNameLimit = 255;

    private const string Separator = " - ";
    private const string FallbackTitle = "SubDL";
    private const string FallbackUploader = "Unknown";

    private static readonly string[] SubtitleExtensions = { "srt", "ass", "ssa", "vtt", "sub" };

    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").Distinct().ToArray();

    // A hyphen standing alone between whitespace or the string ends: what would form " - " once spaces are collapsed.
    private static readonly Regex LoneHyphen = new(@"(?<!\S)-(?!\S)", RegexOptions.Compiled);

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

        // Removing every stand-alone hyphen (not just " - ") guarantees no separator survives,
        // even for input like "A - - B", which a single Replace pass would leave as "A - B".
        var text = LoneHyphen.Replace(builder.ToString(), " ");
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
        const string suffixAfterUploader = ".fa.";
        var fixedBytes = Utf8(videoStem) + 1 + Separator.Length + suffixAfterUploader.Length + Utf8(extension);

        // Shorten the title first, then the uploader if the stem and uploader alone are too long.
        var titleRoom = MaxFileNameLength - fixedBytes - Utf8(cleanUploader);
        if (Utf8(cleanTitle) > titleRoom)
        {
            cleanTitle = TruncateToBytes(cleanTitle, Math.Max(titleRoom, 0));
        }

        if (cleanTitle.Length == 0)
        {
            cleanTitle = FallbackTitle;
        }

        var uploaderRoom = MaxFileNameLength - fixedBytes - Utf8(cleanTitle);
        if (Utf8(cleanUploader) > uploaderRoom)
        {
            cleanUploader = TruncateToBytes(cleanUploader, Math.Max(uploaderRoom, 0));
            if (cleanUploader.Length == 0)
            {
                cleanUploader = FallbackUploader;
            }
        }

        var tail = Separator + cleanUploader + suffixAfterUploader + extension;
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

    /// <summary>
    /// Tells whether a file name is short enough to create. <see cref="BuildFileName"/> only
    /// shortens the title and uploader, so a very long video name can still push it past the limit.
    /// </summary>
    /// <param name="fileName">The file name, without a folder.</param>
    /// <returns>True when the name is at most <see cref="FileSystemNameLimit"/> UTF-8 bytes.</returns>
    public static bool FitsFileSystem(string fileName) => Utf8(fileName) <= FileSystemNameLimit;

    private static int Utf8(string value) => Encoding.UTF8.GetByteCount(value);

    /// <summary>Cuts to at most <paramref name="maxBytes"/> UTF-8 bytes without splitting a surrogate pair.</summary>
    private static string TruncateToBytes(string value, int maxBytes)
    {
        var length = 0;
        var bytes = 0;
        while (length < value.Length)
        {
            var step = char.IsHighSurrogate(value[length]) && length + 1 < value.Length ? 2 : 1;
            var stepBytes = Encoding.UTF8.GetByteCount(value.AsSpan(length, step));
            if (bytes + stepBytes > maxBytes)
            {
                break;
            }

            bytes += stepBytes;
            length += step;
        }

        return value[..length].TrimEnd();
    }
}
