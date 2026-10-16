using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubDL.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the SubDL API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
