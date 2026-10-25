using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Api;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Http;

public sealed class EgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "livehub-eg-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _cfg = new();
    private readonly SecretProtector _secrets;
    private readonly EgressManager _egress;

    public EgressTests()
    {
        _cfg.EnsureDefaults();
        _secrets = new SecretProtector(new LiveHubPaths(Path.Combine(_dir, "d"), Path.Combine(_dir, "c")));
        _egress = new EgressManager(new ConfigAccessor(() => _cfg), _secrets, NullLogger<EgressManager>.Instance);
    }

    public void Dispose()
    {
        _egress.Dispose();
        Directory.Delete(_dir, true);
    }

    /// <summary>Fake internet: answers per host/profile.</summary>
    private sealed class FakeHttp : IOutboundHttp
    {
        public Func<FetchRequest, FetchResponse> Handler { get; set; } = _ => new FetchResponse { StatusCode = 404 };

        public List<FetchRequest> Requests { get; } = new();

        public Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }

        public Task<StreamingResponse> OpenStreamAsync(FetchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static FetchResponse Json(string json) => new() { StatusCode = 200, Body = Encoding.UTF8.GetBytes(json), FinalUrl = new Uri("https://ipinfo.io/json") };

    private EgressDiagnostics Diagnostics(FakeHttp http) => new(http, _egress, new ConfigAccessor(() => _cfg), _secrets, NullLogger<EgressDiagnostics>.Instance);

    private void AddVpn(string id = "vpn", string expected = "IT", bool killSwitch = true)
        => _cfg.EgressProfiles.Add(new EgressProfile { Id = id, Name = "VPN", Type = EgressType.Http, Host = "127.0.0.1", Port = 1, ExpectedCountry = expected, KillSwitch = killSwitch, GluetunControlUrl = "http://gluetun:8000", GluetunApiKey = _secrets.Protect("k3y") });

    [Theory]
    [InlineData("{\"ip\":\"5.6.7.8\",\"city\":\"Milan\",\"country\":\"IT\",\"org\":\"AS1 VPN\"}", "5.6.7.8", "IT", "Milan")]
    [InlineData("{\"status\":\"success\",\"country\":\"Italy\",\"countryCode\":\"IT\",\"city\":\"Rome\",\"query\":\"5.6.7.9\"}", "5.6.7.9", "IT", "Rome")]
    [InlineData("{\"ip\":\"2a01::1\",\"country_iso\":\"DE\"}", "2a01::1", "DE", null)]
    [InlineData("1.2.3.4\n", "1.2.3.4", null, null)]
    [InlineData("<html>blocked</html>", null, null, null)]
    public void ParsesIpServices(string body, string? ip, string? country, string? city)
    {
        var (i, c, ci, _) = EgressDiagnostics.ParseIpInfo(body);
        Assert.Equal(ip, i);
        Assert.Equal(country, c);
        Assert.Equal(city, ci);
    }

    [Fact]
    public async Task CheckReportsIpCountryAndUpdatesHealth()
    {
        AddVpn();
        var http = new FakeHttp { Handler = r => Json(r.EgressProfileId == "vpn" ? "{\"ip\":\"5.6.7.8\",\"country\":\"IT\"}" : "{\"ip\":\"1.1.1.1\",\"country\":\"NL\"}") };
        var result = await Diagnostics(http).CheckAsync("vpn", CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("5.6.7.8", result.Ip);
        Assert.True(result.CountryMatches);
        Assert.False(result.SameAsDirect);
        Assert.Equal("http://127.0.0.1:1", result.Route);
        Assert.All(http.Requests, r => Assert.True(r.HealthProbe));
        var health = _egress.GetHealth("vpn");
        Assert.True(health.Healthy);
        Assert.Equal("IT", health.Country);
    }

    [Fact]
    public async Task VpnNotActiveIsDetectedAndEngagesTheKillSwitch()
    {
        AddVpn(expected: "IT");
        var http = new FakeHttp { Handler = _ => Json("{\"ip\":\"1.1.1.1\",\"country\":\"NL\"}") };
        var result = await Diagnostics(http).CheckAsync("vpn", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.True(result.SameAsDirect);
        Assert.False(result.CountryMatches);
        Assert.False(_egress.GetHealth("vpn").Healthy);

        // Kill switch: normal requests are refused, health probes may still go through the profile.
        Assert.Throws<EgressUnavailableException>(() => _egress.EnsureAvailable("vpn"));
        _egress.EnsureAvailable("vpn", probe: true);

        // Without kill switch the profile keeps being used (still through the proxy, never directly).
        _cfg.EgressProfiles.Single(p => p.Id == "vpn").KillSwitch = false;
        _egress.EnsureAvailable("vpn");
    }

    [Fact]
    public void MissingOrIncompleteProfilesNeverFallBackToDirect()
    {
        Assert.Throws<EgressUnavailableException>(() => _egress.EnsureAvailable("nope"));
        _cfg.EgressProfiles.Add(new EgressProfile { Id = "broken", Type = EgressType.Socks5, Host = string.Empty, Port = 0 });
        Assert.Throws<EgressUnavailableException>(() => _egress.EnsureAvailable("broken"));
        _egress.EnsureAvailable(EgressProfile.DirectId);
    }

    [Fact]
    public async Task KillSwitchBlocksRealRequestsBeforeAnyByteIsSent()
    {
        AddVpn();
        _egress.ReportHealth("vpn", false, "down");
        var sent = 0;
        var handler = new CountingHandler(() => sent++);
        var http = new OutboundHttp(new TestHttpClientFactory(handler), _egress);
        await Assert.ThrowsAsync<EgressUnavailableException>(() => http.FetchAsync(new FetchRequest { Url = new Uri("https://example.org/"), EgressProfileId = "vpn" }, CancellationToken.None));
        Assert.Equal(0, sent);
        await http.FetchAsync(new FetchRequest { Url = new Uri("https://example.org/"), EgressProfileId = "vpn", HealthProbe = true }, CancellationToken.None);
        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task UnreachableProxiesAreReportedByTheMonitor()
    {
        AddVpn();
        var http = new FakeHttp();
        var monitor = new EgressHealthMonitor(_egress, Diagnostics(http), new ConfigAccessor(() => _cfg), NullLogger<EgressHealthMonitor>.Instance);
        await monitor.CheckDueAsync(CancellationToken.None);
        var health = _egress.GetHealth("vpn");
        Assert.False(health.Healthy);
        Assert.Contains("127.0.0.1:1", health.LastError, StringComparison.Ordinal);
        Assert.Throws<EgressUnavailableException>(() => _egress.EnsureAvailable("vpn"));
        Assert.Empty(http.Requests); // no IP check is attempted while the proxy port is closed
    }

    [Fact]
    public async Task GluetunStatusAndCountryChange()
    {
        AddVpn();
        var http = new FakeHttp
        {
            Handler = r => r.Url.AbsolutePath switch
            {
                "/v1/vpn/status" when r.Method == HttpMethod.Get => Json("{\"status\":\"running\"}"),
                "/v1/publicip/ip" => Json("{\"public_ip\":\"5.6.7.8\",\"country\":\"Italy\",\"city\":\"Milan\",\"organization\":\"VPN Ltd\"}"),
                _ => new FetchResponse { StatusCode = 404 },
            },
        };
        var diagnostics = Diagnostics(http);
        var status = await diagnostics.GluetunAsync("vpn", CancellationToken.None);
        Assert.Equal("running", status.Status);
        Assert.Equal("5.6.7.8", status.PublicIp);
        Assert.Equal("Italy", status.Country);
        Assert.False(status.CanChangeCountry);
        Assert.Contains("SERVER_COUNTRIES", status.Instructions, StringComparison.Ordinal);
        Assert.All(http.Requests, r =>
        {
            Assert.Equal("k3y", r.Headers["X-API-Key"]);
            Assert.True(r.AllowPrivateNetwork);
            Assert.Equal(EgressProfile.DirectId, r.EgressProfileId);
        });

        var (ok, message) = await diagnostics.SetGluetunCountryAsync("vpn", "DE", CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("SERVER_COUNTRIES", message, StringComparison.Ordinal);

        http.Handler = r => r.Method == HttpMethod.Put || r.Url.AbsolutePath == "/v1/vpn/settings" ? Json("{}") : Json("{\"status\":\"running\"}");
        (ok, message) = await diagnostics.SetGluetunCountryAsync("vpn", "DE", CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("Germany", message, StringComparison.Ordinal);
        Assert.Contains(http.Requests, r => r.Method == HttpMethod.Put && r.Body!.Contains("\"Germany\"", StringComparison.Ordinal));
        Assert.True(await diagnostics.GluetunRunningAsync(_cfg.EgressProfiles.Single(p => p.Id == "vpn"), CancellationToken.None));
    }

    [Fact]
    public void SavingKeepsMaskedSecretsAndValidates()
    {
        AddVpn();
        var stored = _cfg.EgressProfiles.Single(p => p.Id == "vpn").GluetunApiKey;
        var request = new EgressSaveRequest
        {
            Profiles =
            {
                new EgressProfile { Id = "vpn", Type = EgressType.Socks5, Host = "gluetun", Port = 1080, Username = "u", Password = "secret", GluetunApiKey = SecretProtector.Mask, ExpectedCountry = "italia" },
            },
            DefaultProfileId = "vpn",
            RegionEgress = { new RegionEgressRule { Country = "uk", ProfileId = "vpn" } },
        };
        Assert.Empty(EgressController.Apply(_cfg, request, _secrets));
        var vpn = _cfg.EgressProfiles.Single(p => p.Id == "vpn");
        Assert.Equal(stored, vpn.GluetunApiKey);
        Assert.StartsWith(SecretProtector.Prefix, vpn.Password, StringComparison.Ordinal);
        Assert.Equal("secret", _secrets.Unprotect(vpn.Password));
        Assert.Equal("IT", vpn.ExpectedCountry);
        Assert.Contains(_cfg.EgressProfiles, p => p.Id == EgressProfile.DirectId);
        Assert.Equal("vpn", _cfg.DefaultEgressProfileId);
        Assert.Equal("GB", Assert.Single(_cfg.RegionEgress).Country);
        Assert.Equal("vpn", _egress.ResolveProfileId(null, "GB"));
        Assert.Equal("explicit", _egress.ResolveProfileId("explicit", "GB"));

        var bad = new EgressSaveRequest { Profiles = { new EgressProfile { Id = "x y", Type = EgressType.Http }, new EgressProfile { Id = "p", Type = EgressType.Http, Host = string.Empty } } };
        Assert.Equal(2, EgressController.Apply(_cfg, bad, _secrets).Count);
        Assert.Equal("vpn", _cfg.DefaultEgressProfileId); // unchanged on error
        Assert.Equal("socks5://***@gluetun:1080", EgressManager.Describe(vpn));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Action _onSend;

        public CountingHandler(Action onSend) => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onSend();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}
