using System.Threading;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Diagnostics;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Images;
using Jellyfin.Plugin.LiveHub.LiveTv;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Jellyfin.Plugin.LiveHub.Sync;
using Jellyfin.Plugin.LiveHub.Users;
using Jellyfin.Plugin.LiveHub.Wizard;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiveHub;

/// <summary>
/// Registers the plugin services in the Jellyfin DI container.
/// Scheduled tasks and media source providers are discovered by Jellyfin through type scanning and are not registered here.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LocalizationService>();
        serviceCollection.AddSingleton<LiveHubPaths>();
        serviceCollection.AddSingleton<ConfigAccessor>();
        serviceCollection.AddSingleton<SecretProtector>();

        // Egress: every outgoing request of the plugin goes through the named client "LiveHub.Egress".
        serviceCollection.AddSingleton<EgressManager>();
        serviceCollection.AddSingleton<IEgressGate>(sp => sp.GetRequiredService<EgressManager>());
        serviceCollection.AddSingleton<IEgressResolver>(sp => sp.GetRequiredService<EgressManager>());
        serviceCollection.AddHttpClient(OutboundHttp.ClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan) // per-request timeouts; relay streams are long-lived
            .ConfigurePrimaryHttpMessageHandler(sp => new EgressRoutingHandler(sp.GetRequiredService<EgressManager>()))
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers(); // the default HTTP loggers would write URLs (with credentials) to the log
        serviceCollection.AddSingleton<IOutboundHttp, OutboundHttp>();
        serviceCollection.AddSingleton<EgressDiagnostics>();
        serviceCollection.AddHostedService<EgressHealthMonitor>();

        serviceCollection.AddSingleton<ProviderEngine>();
        serviceCollection.AddSingleton<DefinitionStore>();
        serviceCollection.AddSingleton<WizardService>();

        // Catalog and real-time synchronisation.
        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddSingleton<SnapshotStore>();
        serviceCollection.AddSingleton<SyncScheduler>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());

        // Users and the "Live Hub" channel.
        serviceCollection.AddSingleton<UserPreferenceStore>();
        serviceCollection.AddSingleton<AccessPolicy>();
        serviceCollection.AddSingleton<UserViewFactory>();
        serviceCollection.AddSingleton<ChannelText>();
        serviceCollection.AddSingleton<SkiaImageRenderer>();
        serviceCollection.AddSingleton<ImageProxy>();
        serviceCollection.AddSingleton<EventImageService>();
        serviceCollection.AddSingleton<IUserDirectory, JellyfinUserDirectory>();
        serviceCollection.AddSingleton<UserSettingsService>();
        serviceCollection.AddSingleton<ISessionSink, JellyfinSessionSink>();
        serviceCollection.AddHostedService<NotificationService>();
        serviceCollection.AddSingleton<LiveHubChannel>();
        serviceCollection.AddSingleton<IChannel>(sp => sp.GetRequiredService<LiveHubChannel>());

        // Resolver chain, relay and playback (LiveHubMediaSourceProvider is discovered by type scanning).
        serviceCollection.AddSingleton<DirectResolver>();
        serviceCollection.AddSingleton<PageExtractResolver>();
        serviceCollection.AddSingleton<HeadlessResolver>();
        serviceCollection.AddSingleton<StreamResolverService>();
        serviceCollection.AddSingleton<EventStreamService>();
        serviceCollection.AddSingleton<RelayTokenService>();
        serviceCollection.AddSingleton<IRelayBaseUrl, ServerRelayBaseUrl>();
        serviceCollection.AddSingleton<RelayService>();
        serviceCollection.AddSingleton<IActiveStreamsSource>(sp => sp.GetRequiredService<RelayService>());
        serviceCollection.AddSingleton<MediaSourceBuilder>();
        serviceCollection.AddSingleton<PlaybackTester>();
        serviceCollection.AddHostedService<PreResolveService>();
        serviceCollection.AddSingleton<LiveHubMediaSourceProvider>();

        // Live TV: tuner (24/7 + slot channels), guide source and automatic guide refresh.
        serviceCollection.AddSingleton<LiveTvLineup>();
        serviceCollection.AddSingleton<ITunerHost, LiveHubTunerHost>();
        serviceCollection.AddSingleton<IListingsProvider, LiveHubListingsProvider>();
        serviceCollection.AddSingleton<ILiveTvHost, JellyfinLiveTvHost>();
        serviceCollection.AddHostedService<LiveTvIntegration>();
    }
}
