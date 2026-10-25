using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using LiveHub.MockServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Infrastructure;

/// <summary>A controllable clock.</summary>
public sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>IHttpClientFactory returning clients bound to a test handler.</summary>
public sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
}

/// <summary>Kill switch gate that always allows.</summary>
public sealed class OpenGate : IEgressGate
{
    public void EnsureAvailable(string profileId, bool probe = false)
    {
    }
}

/// <summary>Kill switch gate that is always closed (egress down).</summary>
public sealed class ClosedGate : IEgressGate
{
    public void EnsureAvailable(string profileId, bool probe = false) => throw new EgressUnavailableException($"Egress '{profileId}' is down (test).");
}

/// <summary>Fixed relay base URL.</summary>
public sealed class FixedBaseUrl : Jellyfin.Plugin.LiveHub.Relay.IRelayBaseUrl
{
    public FixedBaseUrl(string url) => BaseUrl = url;

    public string BaseUrl { get; }
}

/// <summary>Egress resolver returning a fixed profile.</summary>
public sealed class FixedEgress : IEgressResolver
{
    private readonly string _profile;

    public FixedEgress(string profile = "direct") => _profile = profile;

    public string ResolveProfileId(string? providerProfile, string? providerCountry) => providerProfile ?? _profile;
}

/// <summary>Starts the mock server in-process (TestServer, no network).</summary>
public sealed class MockServerFixture : IAsyncLifetime
{
    private WebApplication? _app;

    public const string BaseUrl = "http://localhost";

    public HttpMessageHandler Handler { get; private set; } = null!;

    public MockOptions Options { get; } = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        // The default ConsoleLifetime blocks process exit until the host is disposed; tests do not need it.
        builder.Services.AddSingleton<Microsoft.Extensions.Hosting.IHostLifetime, NoopHostLifetime>();
        _app = builder.Build();
        MockServerApp.Map(_app, Options);
        await _app.StartAsync();
        Handler = _app.GetTestServer().CreateHandler();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class NoopHostLifetime : Microsoft.Extensions.Hosting.IHostLifetime
    {
        public Task WaitForStartAsync(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public IOutboundHttp CreateHttp() => new OutboundHttp(new TestHttpClientFactory(Handler), new OpenGate());

    public ProviderEngine CreateEngine(TimeProvider? clock = null)
        => new(CreateHttp(), new FixedEgress(), NullLogger<ProviderEngine>.Instance, clock);
}

public static class Fixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Read(params string[] parts) => File.ReadAllText(Path.Combine(Dir, Path.Combine(parts)));

    /// <summary>Loads a mock definition and points it at the in-process mock server.</summary>
    public static ProviderDefinition Definition(string file)
    {
        var def = DefinitionSerializer.TryParse(Read("definitions", file), out var error) ?? throw new InvalidOperationException(error);
        def.Variables ??= new Dictionary<string, string>();
        def.Variables["mockBase"] = MockServerFixture.BaseUrl;
        return def;
    }

    public static IEnumerable<string> DefinitionFiles() => Directory.EnumerateFiles(Path.Combine(Dir, "definitions"), "*.json");

    public static IEnumerable<string> TemplateFiles() => Directory.EnumerateFiles(Path.Combine(Dir, "templates"), "*.json");
}
