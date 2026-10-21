using Jellyfin.Plugin.SubDL.Movies;
using Xunit;

namespace Jellyfin.Plugin.SubDL.Tests;

public class SubtitlePickerTests
{
    private static SubtitleCandidate C(string id, string? up, double s) => new(id, "T" + id, up, s);

    [Fact]
    public void Pick_KeepsBestPerUploaderAndOrdersByScore()
    {
        var picked = SubtitlePicker.Pick(
            [C("1", "Ali", 0.3), C("2", "Ali", 0.6), C("3", "Sara", 0.5), C("4", "Reza", 0.2)],
            new HashSet<string>(),
            2);
        Assert.Equal(["2", "3"], picked.Select(p => p.Id));
    }

    [Fact]
    public void Pick_TreatsCaseAndSpacingAsSameUploader()
        => Assert.Single(SubtitlePicker.Pick([C("1", "Ali  Rezaei", 0.4), C("2", "ali rezaei", 0.5)], new HashSet<string>(), 5));

    [Fact]
    public void Pick_GroupsMissingAuthorsAsOneUnknown()
        => Assert.Single(SubtitlePicker.Pick([C("1", null, 0.4), C("2", " ", 0.5)], new HashSet<string>(), 5));

    [Fact]
    public void Pick_SkipsUploadersAlreadyOnDisk()
    {
        var have = new HashSet<string> { SubtitlePicker.UploaderKey("Ali") };
        var picked = SubtitlePicker.Pick([C("1", "Ali", 0.9), C("2", "Sara", 0.1)], have, 5);
        Assert.Equal(["2"], picked.Select(p => p.Id));
    }

    [Fact]
    public void Pick_ZeroCountIsEmpty()
        => Assert.Empty(SubtitlePicker.Pick([C("1", "Ali", 0.9)], new HashSet<string>(), 0));
}
