using Jellyfin.Plugin.SubDL.Movies;
using Xunit;

namespace Jellyfin.Plugin.SubDL.Tests;

public class SubtitleNamingTests
{
    [Fact]
    public void BuildFileName_UsesTitleUploaderAndFa()
        => Assert.Equal("Anora.2024.2160p.Anora 2024 1080p BluRay - Ali Rezaei.fa.srt",
            SubtitleNaming.BuildFileName("Anora.2024.2160p", "Anora.2024.1080p.BluRay", "Ali Rezaei", "SRT"));

    [Fact]
    public void Clean_RemovesDotsSoNoTokenIsReadAsAFlag()
        => Assert.Equal("Movie 2024 HI en forced CC WEB", SubtitleNaming.Clean("Movie.2024.HI.en.forced.CC.WEB"));

    [Fact]
    public void Clean_StripsInvalidCharacters()
        => Assert.Equal("Who Cares Yes No", SubtitleNaming.Clean("Who: Cares? \"Yes\" / No|"));

    [Fact]
    public void Clean_ReplacesSeparatorInsideUploader()
        => Assert.Equal("Team A B", SubtitleNaming.Clean("Team A - B"));

    [Fact]
    public void BuildFileName_CapsLengthByShorteningTitle()
    {
        var name = SubtitleNaming.BuildFileName("Stem", new string('x', 400), "Uploader", "srt");
        Assert.True(name.Length <= SubtitleNaming.MaxFileNameLength);
        Assert.StartsWith("Stem.", name);
        Assert.EndsWith(" - Uploader.fa.srt", name);
    }

    [Fact]
    public void BuildFileName_MissingUploaderIsUnknown()
        => Assert.EndsWith(" - Unknown.fa.srt", SubtitleNaming.BuildFileName("S", "T", "  ", "srt"));

    [Fact]
    public void TryGetUploader_RoundTripsBuildFileName()
    {
        var name = SubtitleNaming.BuildFileName("Movie.2024", "Movie 2024 - Extended", "Team A - B", "ass");
        Assert.True(SubtitleNaming.TryGetUploader("Movie.2024", name, out var uploader));
        Assert.Equal("Team A B", uploader);
    }

    [Theory]
    [InlineData("Movie.2024.fa.srt")]            // plain Jellyfin name, not ours
    [InlineData("Other.T - U.fa.srt")]           // different video
    [InlineData("Movie.2024.T - U.en.srt")]      // not Persian
    [InlineData("Movie.2024.T - U.fa.txt")]      // not a subtitle
    public void TryGetUploader_RejectsFilesThatAreNotOurs(string file)
        => Assert.False(SubtitleNaming.TryGetUploader("Movie.2024", file, out _));
}
