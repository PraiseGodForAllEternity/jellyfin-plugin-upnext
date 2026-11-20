using Jellyfin.Plugin.UpNext.Injection;
using Jellyfin.Plugin.UpNext.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.UpNext;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<PreferenceStore>();
        serviceCollection.AddSingleton<ClaimTracker>();
        serviceCollection.AddSingleton<NextMovieService>();
        serviceCollection.AddSingleton<CreditsLocator>();
        serviceCollection.AddHostedService<RemoteAutoplayService>();
        serviceCollection.AddHostedService<FileTransformationRegistration>();
        serviceCollection.AddTransient<IStartupFilter, IndexHtmlStartupFilter>();
    }
}
