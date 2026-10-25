using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Images;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Users;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Jellyfin.Plugin.LiveHub.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.LiveHub.Tests.Infrastructure;

/// <summary>Wires the plugin services against the in-process mock server (no Jellyfin needed).</summary>
public sealed class Harness : IDisposable
{
    public Harness(MockServerFixture mock, TimeProvider? clock = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "livehub-h-" + Guid.NewGuid().ToString("N"));
        Paths = new LiveHubPaths(Path.Combine(Dir, "data"), Path.Combine(Dir, "cache"));
        Config = new PluginConfiguration();
        Config.EnsureDefaults();
        ConfigAccessor = new ConfigAccessor(() => Config);
        Protector = new SecretProtector(Paths);
        Store = new DefinitionStore(Paths, Protector, NullLogger<DefinitionStore>.Instance);
        Engine = mock.CreateEngine(clock);
        Catalog = new CatalogService(ConfigAccessor, NullLogger<CatalogService>.Instance, clock ?? TimeProvider.System);
        Snapshots = new SnapshotStore(Paths, NullLogger<SnapshotStore>.Instance);
        Scheduler = new SyncScheduler(Store, Engine, Catalog, Snapshots, ConfigAccessor, NullLogger<SyncScheduler>.Instance);
        Prefs = new UserPreferenceStore(Paths, NullLogger<UserPreferenceStore>.Instance);
        Access = new AccessPolicy(ConfigAccessor, Store);
        Views = new UserViewFactory(Prefs, Access, ConfigAccessor, Catalog);
        Localization = new LocalizationService();
        Channel = new LiveHubChannel(Catalog, Views, Store, new ChannelText(Localization), new EventImageService(), ConfigAccessor, NullLogger<LiveHubChannel>.Instance);

        Http = mock.CreateHttp();
        var direct = new DirectResolver(Http, clock);
        Resolver = new StreamResolverService(Store, new FixedEgress(), direct, new PageExtractResolver(Http, direct), new HeadlessResolver(Http, direct, ConfigAccessor), NullLogger<StreamResolverService>.Instance, clock);
        Events = new EventStreamService(Catalog, Store, Engine, NullLogger<EventStreamService>.Instance);
        Tokens = new RelayTokenService(Protector, clock);
        Relay = new RelayService(Http, Resolver, Events, Views, ConfigAccessor, Tokens, new FixedBaseUrl(RelayBase), NullLogger<RelayService>.Instance, clock);
        Builder = new MediaSourceBuilder(Relay, Resolver, Store, ConfigAccessor);
    }

    public const string RelayBase = "http://jellyfin.local";

    public IOutboundHttp Http { get; }

    public StreamResolverService Resolver { get; }

    public EventStreamService Events { get; }

    public RelayTokenService Tokens { get; }

    public RelayService Relay { get; }

    public MediaSourceBuilder Builder { get; }

    public UserPreferenceStore Prefs { get; }

    public AccessPolicy Access { get; }

    public UserViewFactory Views { get; }

    public LocalizationService Localization { get; }

    public LiveHubChannel Channel { get; }

    public string Dir { get; }

    public LiveHubPaths Paths { get; }

    public PluginConfiguration Config { get; }

    public ConfigAccessor ConfigAccessor { get; }

    public SecretProtector Protector { get; }

    public DefinitionStore Store { get; }

    public ProviderEngine Engine { get; }

    public CatalogService Catalog { get; }

    public SnapshotStore Snapshots { get; }

    public SyncScheduler Scheduler { get; }

    public void AddAllMockDefinitions()
    {
        foreach (var file in Fixtures.DefinitionFiles().Select(Path.GetFileName))
        {
            var result = Store.Save(Fixtures.Definition(file!));
            if (!result.IsValid)
            {
                throw new InvalidOperationException(string.Join("; ", result.Errors));
            }
        }
    }

    public void Dispose()
    {
        Relay.Dispose();
        Scheduler.Dispose();
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}
