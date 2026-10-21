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

    [Theory]
    [InlineData("A - - B", "A B")]
    [InlineData("- B", "B")]
    [InlineData("A -", "A")]
    [InlineData("A-B", "A-B")]
    public void Clean_LeavesNoSeparator(string input, string expected)
    {
        Assert.Equal(expected, SubtitleNaming.Clean(input));
        Assert.DoesNotContain(" - ", SubtitleNaming.Clean(input));
    }

    [Fact]
    public void TryGetUploader_RoundTripsUploaderWithRepeatedSeparator()
    {
        var name = SubtitleNaming.BuildFileName("Movie.2024", "T", "A - - B", "srt");
        Assert.True(SubtitleNaming.TryGetUploader("Movie.2024", name, out var uploader));
        Assert.Equal("A B", uploader);
    }

    [Fact]
    public void BuildFileName_PersianUploaderStaysWithinByteLimit()
    {
        var name = SubtitleNaming.BuildFileName("Movie.2024", "Title", new string('ف', 150), "srt");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= SubtitleNaming.MaxFileNameLength);
        Assert.EndsWith(".fa.srt", name);
        Assert.True(SubtitleNaming.TryGetUploader("Movie.2024", name, out var uploader));
        Assert.NotEmpty(uploader);
    }

    [Fact]
    public void BuildFileName_DoesNotSplitSurrogatePair()
    {
        var name = SubtitleNaming.BuildFileName("S", string.Concat(Enumerable.Repeat("😀", 100)), "U", "srt");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= SubtitleNaming.MaxFileNameLength);
        Assert.DoesNotContain('�', name);
        Assert.Equal(name, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(name)));
    }

    [Fact]
    public void BuildFileName_EmptyTitleBecomesSubDL()
        => Assert.Equal("S.SubDL - U.fa.srt", SubtitleNaming.BuildFileName("S", "...", "U", "srt"));

    [Fact]
    public void TryGetUploader_AcceptsUpperCase()
        => Assert.True(SubtitleNaming.TryGetUploader("Movie.2024", "MOVIE.2024.T - U.FA.SRT", out _));

    [Fact]
    public void FitsFileSystem_AcceptsExactly255Bytes()
        => Assert.True(SubtitleNaming.FitsFileSystem(new string('a', 255)));

    [Fact]
    public void FitsFileSystem_Rejects256Bytes()
        => Assert.False(SubtitleNaming.FitsFileSystem(new string('a', 256)));

    [Fact]
    public void FitsFileSystem_CountsPersianAsTwoBytes()
    {
        // 128 Persian letters are 128 characters but 256 UTF-8 bytes.
        Assert.False(SubtitleNaming.FitsFileSystem(new string('ف', 128)));
        Assert.True(SubtitleNaming.FitsFileSystem(new string('ف', 127)));
    }

    [Fact]
    public void FitsFileSystem_RejectsNameBuiltForAVeryLongVideoName()
    {
        // The builder shortens title and uploader only, so a 240-byte stem still overflows.
        var name = SubtitleNaming.BuildFileName(new string('s', 240), "Title", "Uploader", "srt");
        Assert.False(SubtitleNaming.FitsFileSystem(name));
    }

    [Fact]
    public void FitsFileSystem_AcceptsNormalName()
        => Assert.True(SubtitleNaming.FitsFileSystem(
            SubtitleNaming.BuildFileName("Anora.2024.2160p.AMZN.WEB-DL", new string('x', 400), "Ali Rezaei", "srt")));
}
