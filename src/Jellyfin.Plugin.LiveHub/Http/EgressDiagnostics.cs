using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>
/// "Verify connection" (public IP and exit country through a profile, leak detection against the direct IP), the
/// gluetun control server client and the cheap reachability probe used by the health monitor.
/// </summary>
public sealed class EgressDiagnostics
{
    private const string GluetunInstructions = "Gluetun non permette di cambiare paese a runtime in questa versione: imposta SERVER_COUNTRIES nel servizio gluetun del docker-compose e ricrea il container (docker compose up -d gluetun). / Set SERVER_COUNTRIES in the gluetun service and recreate the container.";

    private readonly IOutboundHttp _http;
    private readonly EgressManager _egress;
    private readonly ConfigAccessor _config;
    private readonly SecretProtector _secrets;
    private readonly ILogger<EgressDiagnostics> _logger;
    private readonly TimeProvider _clock;
    private (DateTimeOffset At, string? Ip) _directIp;

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressDiagnostics"/> class.
    /// </summary>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="egress">Egress manager.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="secrets">Secrets (gluetun API key).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock.</param>
    public EgressDiagnostics(IOutboundHttp http, EgressManager egress, ConfigAccessor config, SecretProtector secrets, ILogger<EgressDiagnostics> logger, TimeProvider? clock = null)
    {
        _http = http;
        _egress = egress;
        _config = config;
        _secrets = secrets;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Parses the answer of an IP information service (ipinfo.io, ip-api.com, ifconfig.co, ipify or plain text).</summary>
    /// <param name="body">The body.</param>
    /// <returns>IP, country (ISO), city and organization.</returns>
    public static (string? Ip, string? Country, string? City, string? Org) ParseIpInfo(string body)
    {
        var text = (body ?? string.Empty).Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                var json = JsonNode.Parse(text) as JsonObject;
                string? Str(params string[] names) => names.Select(n => json?[n]).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : v.ToString()).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                var ip = Str("ip", "query", "ip_addr", "public_ip", "address");
                var country = Str("country_iso", "countryCode", "country_code", "country");
                return (ip, LocaleNormalizer.NormalizeCountry(country), Str("city"), Str("org", "organization", "isp", "asn_org"));
            }
            catch (JsonException)
            {
                return (null, null, null, null);
            }
        }

        return IPAddress.TryParse(text, out _) ? (text, null, null, null) : (null, null, null, null);
    }

    /// <summary>Verifies a profile: public IP, exit country and (for proxies) that the IP differs from the direct one.</summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result (also recorded as the profile health).</returns>
    public async Task<EgressCheckResult> CheckAsync(string profileId, CancellationToken cancellationToken)
    {
        var result = new EgressCheckResult { ProfileId = profileId };
        var profile = _egress.GetProfile(profileId);
        if (profile is null)
        {
            result.Error = "Unknown egress profile.";
            return result;
        }

        result.Route = EgressManager.Describe(profile);
        result.Expected = LocaleNormalizer.NormalizeCountry(profile.ExpectedCountry);
        var sw = Stopwatch.StartNew();
        try
        {
            var info = await LookupAsync(profileId, cancellationToken).ConfigureAwait(false);
            result.LatencyMs = sw.Elapsed.TotalMilliseconds;
            (result.Ip, result.Country, result.City, result.Org) = info;
            result.Ok = result.Ip is not null;
            if (result.Ip is null)
            {
                result.Error = "The IP check service returned no address.";
            }
            else if (profile.Type != EgressType.Direct)
            {
                var direct = await DirectIpAsync(cancellationToken).ConfigureAwait(false);
                if (direct is not null && string.Equals(direct, result.Ip, StringComparison.Ordinal))
                {
                    result.SameAsDirect = true;
                    result.Ok = false;
                    result.Error = "The proxy exits with the server's own public IP: the VPN is not active. / Il proxy esce con l'IP pubblico del server: la VPN non è attiva.";
                }
            }

            if (result.Expected is not null && result.Country is not null)
            {
                result.CountryMatches = string.Equals(result.Expected, result.Country, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or EgressUnavailableException or ResponseTooLargeException or System.IO.IOException)
        {
            result.LatencyMs = sw.Elapsed.TotalMilliseconds;
            result.Error = Redactor.Redact(ex.Message);
        }

        _egress.ReportHealth(profileId, result.Ok || profile.Type == EgressType.Direct, result.Ok ? null : result.Error);
        var health = _egress.GetHealth(profileId);
        health.PublicIp = result.Ip;
        health.Country = result.Country;
        health.LatencyMs = result.LatencyMs;
        return result;
    }

    /// <summary>Checks that the proxy of a profile accepts TCP connections (cheap periodic probe).</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Null when reachable, else the error.</returns>
    public static async Task<string?> ProbeTcpAsync(EgressProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Type == EgressType.Direct)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(profile.Host, profile.Port, cts.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
                ? $"Proxy {profile.Host}:{profile.Port} does not answer (timeout)."
                : $"Proxy {profile.Host}:{profile.Port} unreachable: {ex.Message}";
        }
    }

    /// <summary>Reads the tunnel status from the gluetun control server of a profile.</summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status.</returns>
    public async Task<GluetunStatus> GluetunAsync(string profileId, CancellationToken cancellationToken)
    {
        var status = new GluetunStatus();
        var profile = _egress.GetProfile(profileId);
        if (profile is null || !Uri.TryCreate(profile.GluetunControlUrl, UriKind.Absolute, out var baseUri))
        {
            status.Error = "No gluetun control server configured for this profile.";
            return status;
        }

        try
        {
            var vpn = await GluetunGetAsync(profile, baseUri, "v1/vpn/status", cancellationToken).ConfigureAwait(false)
                ?? await GluetunGetAsync(profile, baseUri, "v1/openvpn/status", cancellationToken).ConfigureAwait(false);
            status.Status = vpn?["status"]?.GetValue<string>();
            var ip = await GluetunGetAsync(profile, baseUri, "v1/publicip/ip", cancellationToken).ConfigureAwait(false);
            if (ip is not null)
            {
                status.PublicIp = ip["public_ip"]?.GetValue<string>();
                status.Country = ip["country"]?.GetValue<string>();
                status.Region = ip["region"]?.GetValue<string>();
                status.City = ip["city"]?.GetValue<string>();
                status.Organization = ip["organization"]?.GetValue<string>();
            }

            status.CanChangeCountry = await GluetunGetAsync(profile, baseUri, "v1/vpn/settings", cancellationToken).ConfigureAwait(false) is not null;
            if (!status.CanChangeCountry)
            {
                status.Instructions = GluetunInstructions;
            }

            if (status.Status is null)
            {
                status.Error = "The gluetun control server did not answer (check the URL, the HTTP_CONTROL_SERVER_* settings and the API key).";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or JsonException or InvalidOperationException or FormatException)
        {
            status.Error = Redactor.Redact(ex.Message);
        }

        return status;
    }

    /// <summary>Asks gluetun to connect to another country, when the running version supports it.</summary>
    /// <param name="profileId">The profile.</param>
    /// <param name="country">The country (gluetun expects English names, e.g. "Italy").</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it worked, and a message.</returns>
    public async Task<(bool Ok, string Message)> SetGluetunCountryAsync(string profileId, string country, CancellationToken cancellationToken)
    {
        var profile = _egress.GetProfile(profileId);
        if (profile is null || !Uri.TryCreate(profile.GluetunControlUrl, UriKind.Absolute, out var baseUri) || string.IsNullOrWhiteSpace(country))
        {
            return (false, "No gluetun control server or country.");
        }

        var name = CountryName(country);
        var body = new JsonObject { ["provider"] = new JsonObject { ["server_selection"] = new JsonObject { ["countries"] = new JsonArray(name) } } };
        try
        {
            var response = await _http.FetchAsync(GluetunRequest(profile, new Uri(baseUri, "v1/vpn/settings"), HttpMethod.Put, body.ToJsonString()), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                return (false, GluetunInstructions);
            }

            // Reconnect so that the new selection is applied, then re-verify the profile.
            await _http.FetchAsync(GluetunRequest(profile, new Uri(baseUri, "v1/vpn/status"), HttpMethod.Put, "{\"status\":\"stopped\"}"), cancellationToken).ConfigureAwait(false);
            await _http.FetchAsync(GluetunRequest(profile, new Uri(baseUri, "v1/vpn/status"), HttpMethod.Put, "{\"status\":\"running\"}"), cancellationToken).ConfigureAwait(false);
            _egress.ReportHealth(profileId, false, "Reconnecting to " + name);
            return (true, "Gluetun: " + name);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException)
        {
            return (false, Redactor.Redact(ex.Message) + " — " + GluetunInstructions);
        }
    }

    /// <summary>Reports whether the gluetun tunnel of a profile is running (null when no control server is configured or it does not answer).</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>True/false or null.</returns>
    public async Task<bool?> GluetunRunningAsync(EgressProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Uri.TryCreate(profile.GluetunControlUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        try
        {
            var vpn = await GluetunGetAsync(profile, baseUri, "v1/vpn/status", cancellationToken).ConfigureAwait(false);
            var s = vpn?["status"]?.GetValue<string>();
            return s is null ? null : string.Equals(s, "running", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string CountryName(string country)
    {
        var iso = LocaleNormalizer.NormalizeCountry(country);
        if (iso is null)
        {
            return country.Trim();
        }

        try
        {
            return new RegionInfo(iso).EnglishName;
        }
        catch (ArgumentException)
        {
            return country.Trim();
        }
    }

    private async Task<(string? Ip, string? Country, string? City, string? Org)> LookupAsync(string profileId, CancellationToken cancellationToken)
    {
        var url = Uri.TryCreate(_config.Current.IpCheckUrl, UriKind.Absolute, out var u) ? u : new Uri("https://ipinfo.io/json");
        var response = await _http.FetchAsync(
            new FetchRequest
            {
                ProviderId = "egress-check",
                Url = url,
                EgressProfileId = profileId,
                HealthProbe = true,
                Timeout = TimeSpan.FromSeconds(10),
                MaxBytes = 64 * 1024,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" },
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            throw new HttpRequestException($"IP check service answered HTTP {response.StatusCode}.");
        }

        return ParseIpInfo(response.Text());
    }

    private async Task<string?> DirectIpAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (_directIp.Ip is not null && now - _directIp.At < TimeSpan.FromMinutes(10))
        {
            return _directIp.Ip;
        }

        try
        {
            var ip = (await LookupAsync(EgressProfile.DirectId, cancellationToken).ConfigureAwait(false)).Ip;
            _directIp = (now, ip);
            return ip;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or EgressUnavailableException)
        {
            _logger.LogDebug("Direct IP lookup failed: {Error}", ex.Message);
            return null;
        }
    }

    private FetchRequest GluetunRequest(EgressProfile profile, Uri url, HttpMethod method, string? body = null)
    {
        var request = new FetchRequest
        {
            ProviderId = "gluetun",
            Url = url,
            Method = method,
            Body = body,
            ContentType = body is null ? null : "application/json",
            AllowPrivateNetwork = true,
            EgressProfileId = EgressProfile.DirectId,
            HealthProbe = true,
            Timeout = TimeSpan.FromSeconds(8),
            MaxBytes = 256 * 1024,
        };
        var key = _secrets.Unprotect(profile.GluetunApiKey);
        if (!string.IsNullOrEmpty(key))
        {
            request.Headers["X-API-Key"] = key;
        }

        return request;
    }

    private async Task<JsonNode?> GluetunGetAsync(EgressProfile profile, Uri baseUri, string path, CancellationToken cancellationToken)
    {
        var response = await _http.FetchAsync(GluetunRequest(profile, new Uri(baseUri, path), HttpMethod.Get), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(response.Text());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
