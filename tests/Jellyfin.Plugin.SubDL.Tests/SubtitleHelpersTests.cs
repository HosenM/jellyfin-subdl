using Jellyfin.Plugin.SubDL.Providers;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.SubDL.Tests;

public class SubtitleHelpersTests
{
    [Theory]
    [InlineData(VideoContentType.Movie, 3, true)]
    [InlineData(VideoContentType.Movie, 0, false)]
    [InlineData(VideoContentType.Episode, 3, false)]
    public void IsLeftToMovieTask(VideoContentType type, int perMovie, bool expected)
        => Assert.Equal(expected, SubtitleHelpers.IsLeftToMovieTask(type, perMovie));
}
