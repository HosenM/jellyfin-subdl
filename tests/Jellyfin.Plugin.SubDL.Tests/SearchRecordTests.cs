using Jellyfin.Plugin.SubDL.Movies;
using Xunit;

namespace Jellyfin.Plugin.SubDL.Tests;

public sealed class SearchRecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "finished-movies.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        var record = SearchRecord.Load(FilePath);

        Assert.False(record.IsFinished(Guid.NewGuid()));
    }

    [Fact]
    public void Load_CorruptFile_IsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{not json");

        var record = SearchRecord.Load(FilePath);

        Assert.False(record.IsFinished(Guid.NewGuid()));
    }

    [Fact]
    public void Load_EmptyFile_IsEmpty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, string.Empty);

        var record = SearchRecord.Load(FilePath);

        Assert.False(record.IsFinished(Guid.NewGuid()));
    }

    [Fact]
    public void MarkFinished_PersistsAcrossLoad()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        SearchRecord.Load(FilePath).MarkFinished(a);
        var reloaded = SearchRecord.Load(FilePath);

        Assert.True(reloaded.IsFinished(a));
        Assert.False(reloaded.IsFinished(b));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }
}
