using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubDL.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the subdl API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subsource.net API key. Optional; when empty the SubSource
    /// provider stays out of the way.
    /// </summary>
    public string SubSourceApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where orphaned subtitle files are moved to.
    /// </summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many subtitles the scheduled task keeps per movie. 0 turns the movie
    /// task off and leaves movies to the regular providers.
    /// </summary>
    public int SubtitlesPerMovie { get; set; }
}
