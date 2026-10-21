using System.Text.Json;

namespace Jellyfin.Plugin.SubDL.Movies;

/// <summary>
/// Remembers which movies have already been searched so that each is searched only once.
/// Stored as a JSON array of GUIDs.
/// </summary>
internal sealed class SearchRecord
{
    private readonly string _path;
    private readonly HashSet<Guid> _finished;

    private SearchRecord(string path, HashSet<Guid> finished)
    {
        _path = path;
        _finished = finished;
    }

    /// <summary>
    /// Reads the record from <paramref name="path"/>. A missing, empty or unreadable file gives an
    /// empty record: losing it only means movies are searched again, which is safe.
    /// </summary>
    /// <param name="path">The record file.</param>
    /// <returns>The loaded record.</returns>
    public static SearchRecord Load(string path)
    {
        var finished = new HashSet<Guid>();
        try
        {
            if (File.Exists(path))
            {
                var ids = JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(path));
                if (ids is not null)
                {
                    finished.UnionWith(ids);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            finished.Clear();
        }

        return new SearchRecord(path, finished);
    }

    /// <summary>Tells whether the movie has already been searched.</summary>
    /// <param name="movieId">The movie's item id.</param>
    /// <returns>True when it was marked finished.</returns>
    public bool IsFinished(Guid movieId) => _finished.Contains(movieId);

    /// <summary>Marks the movie finished and saves straight away, so a crash loses no progress.</summary>
    /// <param name="movieId">The movie's item id.</param>
    public void MarkFinished(Guid movieId)
    {
        if (!_finished.Add(movieId))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target and move over it, so a crash cannot leave a half-written file.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_finished));
        File.Move(temp, _path, overwrite: true);
    }
}
