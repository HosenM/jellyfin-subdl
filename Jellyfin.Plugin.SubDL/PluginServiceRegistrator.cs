using System.Net.Http.Headers;
using Jellyfin.Plugin.SubDL.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SubDL;

/// <summary>
/// Registers the plugin's services with Jellyfin's container. Jellyfin 12 does not discover
/// plugin services by itself; without this the providers are never called.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(PluginHttp.ClientName, client =>
        {
            // Both services sit behind Cloudflare, which rejects requests that carry no User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Jellyfin-Plugin-SubDL",
                typeof(PluginServiceRegistrator).Assembly.GetName().Version!.ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // The providers and the scheduled movie task share these, so the subdl quota cache is shared too.
        serviceCollection.AddSingleton<SubdlClient>();
        serviceCollection.AddSingleton<SubSourceClient>();

        // Jellyfin asks providers in SubtitleFetcherOrder and stops at the first that returns
        // results, so SubDL first with SubSource second gives fallback without extra logic.
        serviceCollection.AddSingleton<ISubtitleProvider, SubdlSubtitleProvider>();
        serviceCollection.AddSingleton<ISubtitleProvider, SubSourceSubtitleProvider>();
    }
}
