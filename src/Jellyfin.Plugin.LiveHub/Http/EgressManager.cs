using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>
/// Owns one connection handler per egress profile (direct, HTTP(S) proxy, SOCKS5) and the kill switch state.
/// Direct connections validate the resolved IP at connect time (DNS rebinding safe SSRF protection).
/// With SOCKS5 the host name is sent to the proxy, so DNS resolution happens on the proxy side.
/// </summary>
public sealed class EgressManager : IEgressGate, Engine.IEgressResolver, IDisposable
{
    /// <summary>Request option carrying the egress profile id.</summary>
    public static readonly HttpRequestOptionsKey<string> ProfileOption = new("LiveHub.Egress");

    /// <summary>Request option allowing private destinations.</summary>
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateOption = new("LiveHub.AllowPrivate");

    /// <summary>Request option marking a health probe.</summary>
    public static readonly HttpRequestOptionsKey<bool> ProbeOption = new("LiveHub.Probe");

    private readonly ConfigAccessor _config;
    private readonly SecretProtector _secrets;
    private readonly ILogger<EgressManager> _logger;
    private readonly ConcurrentDictionary<string, (string Fingerprint, HttpMessageInvoker Invoker, SocketsHttpHandler Handler)> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EgressHealth> _health = new(StringComparer.Ordinal);
    private readonly List<(DateTimeOffset RetireAt, HttpMessageInvoker Invoker)> _retired = new();
    private readonly Lock _retireLock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressManager"/> class.
    /// </summary>
    /// <param name="config">Configuration accessor.</param>
    /// <param name="secrets">Secret protector (proxy passwords).</param>
    /// <param name="logger">Logger.</param>
    public EgressManager(ConfigAccessor config, SecretProtector secrets, ILogger<EgressManager> logger)
    {
        _config = config;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Resolves the profile of a provider: explicit profile, then region rule, then default.</summary>
    /// <param name="providerProfile">The provider's profile id.</param>
    /// <param name="providerCountry">The provider's default country.</param>
    /// <returns>The profile id.</returns>
    public string ResolveProfileId(string? providerProfile, string? providerCountry)
    {
        if (!string.IsNullOrWhiteSpace(providerProfile))
        {
            return providerProfile;
        }

        var cfg = _config.Current;
        if (!string.IsNullOrWhiteSpace(providerCountry))
        {
            var rule = cfg.RegionEgress.FirstOrDefault(r => string.Equals(r.Country, providerCountry, StringComparison.OrdinalIgnoreCase));
            if (rule is not null && !string.IsNullOrWhiteSpace(rule.ProfileId))
            {
                return rule.ProfileId;
            }
        }

        return string.IsNullOrWhiteSpace(cfg.DefaultEgressProfileId) ? EgressProfile.DirectId : cfg.DefaultEgressProfileId;
    }

    /// <summary>Gets a profile by id.</summary>
    /// <param name="profileId">The id.</param>
    /// <returns>The profile or null.</returns>
    public EgressProfile? GetProfile(string profileId)
    {
        if (string.Equals(profileId, EgressProfile.DirectId, StringComparison.Ordinal))
        {
            return _config.Current.EgressProfiles.FirstOrDefault(p => p.Id == EgressProfile.DirectId) ?? EgressProfile.CreateDirect();
        }

        return _config.Current.EgressProfiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
    }

    /// <summary>Gets the health of a profile.</summary>
    /// <param name="profileId">The id.</param>
    /// <returns>The health.</returns>
    public EgressHealth GetHealth(string profileId) => _health.GetOrAdd(profileId, _ => new EgressHealth());

    /// <summary>Records the result of a health check.</summary>
    /// <param name="profileId">The id.</param>
    /// <param name="healthy">Whether it works.</param>
    /// <param name="error">The error.</param>
    public void ReportHealth(string profileId, bool healthy, string? error)
    {
        var h = GetHealth(profileId);
        if (h.Healthy != healthy)
        {
            if (healthy)
            {
                _logger.LogInformation("Egress profile {Profile} is healthy again", profileId);
            }
            else
            {
                _logger.LogWarning("Egress profile {Profile} is down, kill switch engaged: {Error}", profileId, error);
            }
        }

        h.Healthy = healthy;
        h.LastError = error;
        h.LastCheck = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public void EnsureAvailable(string profileId, bool probe = false)
    {
        var profile = GetProfile(profileId)
            ?? throw new EgressUnavailableException($"Egress profile '{profileId}' does not exist: requests are blocked (no fallback to the direct connection).");
        if (profile.Type == EgressType.Direct)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Port <= 0)
        {
            throw new EgressUnavailableException($"Egress profile '{profile.Name}' has no proxy host/port: requests are blocked.");
        }

        if (!probe && profile.KillSwitch && !GetHealth(profileId).Healthy)
        {
            throw new EgressUnavailableException($"Egress profile '{profile.Name}' is unreachable: kill switch active, no request leaves through the direct connection. {GetHealth(profileId).LastError}");
        }
    }

    /// <summary>Gets the invoker for a profile, (re)building it when the profile changed.</summary>
    /// <param name="profileId">The id.</param>
    /// <returns>The invoker.</returns>
    public HttpMessageInvoker GetInvoker(string profileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var profile = GetProfile(profileId)
            ?? throw new EgressUnavailableException($"Egress profile '{profileId}' does not exist.");
        var fingerprint = $"{profile.Type}|{profile.Host}|{profile.Port}|{profile.Username}|{profile.Password}";
        if (_handlers.TryGetValue(profileId, out var existing) && existing.Fingerprint == fingerprint)
        {
            return existing.Invoker;
        }

        var handler = CreateHandler(profile);
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        _handlers[profileId] = (fingerprint, invoker, handler);
        if (existing.Invoker is not null)
        {
            Retire(existing.Invoker);
        }

        return invoker;
    }

    /// <summary>Builds the proxy URI shown to users (credentials masked).</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The description.</returns>
    public static string Describe(EgressProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Type switch
        {
            EgressType.Direct => "direct",
            EgressType.Socks5 => $"socks5://{(string.IsNullOrEmpty(profile.Username) ? string.Empty : "***@")}{profile.Host}:{profile.Port}",
            EgressType.Https => $"https://{(string.IsNullOrEmpty(profile.Username) ? string.Empty : "***@")}{profile.Host}:{profile.Port}",
            _ => $"http://{(string.IsNullOrEmpty(profile.Username) ? string.Empty : "***@")}{profile.Host}:{profile.Port}",
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var h in _handlers.Values)
        {
            h.Invoker.Dispose();
        }

        lock (_retireLock)
        {
            foreach (var r in _retired)
            {
                r.Invoker.Dispose();
            }

            _retired.Clear();
        }
    }

    private void Retire(HttpMessageInvoker invoker)
    {
        lock (_retireLock)
        {
            var now = DateTimeOffset.UtcNow;
            _retired.Add((now.AddMinutes(2), invoker));
            foreach (var r in _retired.Where(r => r.RetireAt <= now).ToList())
            {
                r.Invoker.Dispose();
                _retired.Remove(r);
            }
        }
    }

    private SocketsHttpHandler CreateHandler(EgressProfile profile)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxConnectionsPerServer = 24,
        };

        if (profile.Type == EgressType.Direct)
        {
            handler.UseProxy = false;
            handler.ConnectCallback = ConnectDirectAsync;
            return handler;
        }

        var scheme = profile.Type switch
        {
            EgressType.Socks5 => "socks5",
            EgressType.Https => "https",
            _ => "http",
        };
        var proxy = new WebProxy(new Uri($"{scheme}://{profile.Host}:{profile.Port}"), BypassOnLocal: false);
        if (!string.IsNullOrEmpty(profile.Username))
        {
            proxy.Credentials = new NetworkCredential(profile.Username, _secrets.Unprotect(profile.Password));
        }

        handler.UseProxy = true;
        handler.Proxy = proxy;
        handler.DefaultProxyCredentials = proxy.Credentials;
        return handler;
    }

    private static async ValueTask<System.IO.Stream> ConnectDirectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(AllowPrivateOption, out var ap) && ap;
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        var allowed = allowPrivate ? addresses : addresses.Where(a => !SsrfGuard.IsForbidden(a)).ToArray();
        if (allowed.Length == 0)
        {
            throw new SsrfException($"'{host}' resolves only to private or local addresses.");
        }

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw new HttpRequestException($"Unable to connect to {host}.", last);
    }
}
