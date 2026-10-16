using System.Globalization;

namespace Jellyfin.Plugin.SubDL.Archive;

/// <summary>
/// Finds subtitle files whose video is gone and moves them out of the library.
/// </summary>
internal static class OrphanArchiver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg", ".flv", ".iso", ".strm"
    };

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub"
    };

    // A subtitle may carry several trailing tags before its extension
    // ("Movie.2024.Group.en.forced.srt"), so a few are peeled off looking for the video.
    private const int MaxSuffixTrims = 4;

    /// <summary>
    /// Lists subtitle files that have no matching video beside them.
    /// </summary>
    /// <param name="locations">Library folders.</param>
    /// <returns>Full paths of orphaned subtitles.</returns>
    public static IReadOnlyList<string> FindOrphans(IEnumerable<string> locations)
    {
        var orphans = new List<string>();

        foreach (var root in Collapse(locations))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            orphans.AddRange(FindOrphansIn(root));
        }

        return orphans;
    }

    /// <summary>
    /// Moves the given files into the archive folder, never overwriting.
    /// </summary>
    /// <param name="orphans">Files to move.</param>
    /// <param name="archivePath">Destination folder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    public static ArchiveOutcome Archive(IReadOnlyList<string> orphans, string archivePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(directory);

        var moved = 0;
        var errors = new List<string>();

        foreach (var source in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                File.Move(source, UniqueDestination(directory, Path.GetFileName(source)));
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1}", Path.GetFileName(source), ex.Message));
            }
        }

        return new ArchiveOutcome(moved, errors);
    }

    /// <summary>
    /// Drops library folders that sit inside another one, so nested libraries are scanned once.
    /// </summary>
    private static List<string> Collapse(IEnumerable<string> locations)
    {
        var ordered = locations
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Path.TrimEndingDirectorySeparator(Path.GetFullPath(l)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l.Length)
            .ToList();

        var kept = new List<string>();

        foreach (var candidate in ordered)
        {
            var nested = kept.Exists(k => candidate.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.Ordinal));

            if (!nested)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    private static List<string> FindOrphansIn(string root)
    {
        var videoStems = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var subtitles = new List<string>();

        foreach (var file in EnumerateFilesSafe(root))
        {
            var extension = Path.GetExtension(file);

            if (SubtitleExtensions.Contains(extension))
            {
                subtitles.Add(file);
            }
            else if (VideoExtensions.Contains(extension))
            {
                var directory = Path.GetDirectoryName(file);
                if (directory is null)
                {
                    continue;
                }

                if (!videoStems.TryGetValue(directory, out var stems))
                {
                    stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    videoStems[directory] = stems;
                }

                stems.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        var orphans = new List<string>();

        foreach (var subtitle in subtitles)
        {
            var directory = Path.GetDirectoryName(subtitle);

            if (directory is null
                || !videoStems.TryGetValue(directory, out var stems)
                || !HasVideo(stems, Path.GetFileNameWithoutExtension(subtitle)))
            {
                orphans.Add(subtitle);
            }
        }

        return orphans;
    }

    /// <summary>
    /// Walks the tree without letting one unreadable folder abort the scan, and without
    /// following reparse points into a loop.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                bool isLink;
                try
                {
                    isLink = (File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (!isLink)
                {
                    pending.Push(subdirectory);
                }
            }
        }
    }

    private static bool HasVideo(HashSet<string> videoStems, string name)
    {
        var candidate = name;

        for (var i = 0; i <= MaxSuffixTrims; i++)
        {
            if (videoStems.Contains(candidate))
            {
                return true;
            }

            var lastDot = candidate.LastIndexOf('.');
            if (lastDot <= 0)
            {
                break;
            }

            candidate = candidate[..lastDot];
        }

        return false;
    }

    private static string UniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (File.Exists(destination))
        {
            destination = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0}.{1}{2}", stem, counter, extension));
            counter++;
        }

        return destination;
    }

    /// <summary>
    /// Result of an archive run.
    /// </summary>
    /// <param name="Moved">Files moved.</param>
    /// <param name="Errors">Per file failures.</param>
    internal sealed record ArchiveOutcome(int Moved, IReadOnlyList<string> Errors);
}
