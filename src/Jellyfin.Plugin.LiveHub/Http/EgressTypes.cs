using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>Kill switch gate consulted before every outgoing request.</summary>
public interface IEgressGate
{
    /// <summary>Throws <see cref="EgressUnavailableException"/> when the profile must not be used.</summary>
    /// <param name="profileId">The profile id.</param>
    /// <param name="probe">True for health probes: they may use an unhealthy profile (that is how recovery is detected).</param>
    void EnsureAvailable(string profileId, bool probe = false);
}

/// <summary>Health state of an egress profile.</summary>
public sealed class EgressHealth
{
    /// <summary>Gets or sets a value indicating whether the profile is usable.</summary>
    public bool Healthy { get; set; } = true;

    /// <summary>Gets or sets the last check time.</summary>
    public DateTimeOffset? LastCheck { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the public IP seen from outside.</summary>
    public string? PublicIp { get; set; }

    /// <summary>Gets or sets the exit country seen from outside.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the round-trip latency of the last check.</summary>
    public double? LatencyMs { get; set; }
}

/// <summary>
/// Primary handler of the named HttpClient "LiveHub.Egress": routes each request to the handler of its egress profile.
/// </summary>
public sealed class EgressRoutingHandler : HttpMessageHandler
{
    private readonly EgressManager _egress;

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressRoutingHandler"/> class.
    /// </summary>
    /// <param name="egress">The egress manager.</param>
    public EgressRoutingHandler(EgressManager egress)
    {
        _egress = egress;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Options.TryGetValue(EgressManager.ProfileOption, out var p) && !string.IsNullOrEmpty(p) ? p : EgressProfile.DirectId;
        _egress.EnsureAvailable(profile, request.Options.TryGetValue(EgressManager.ProbeOption, out var probe) && probe);
        return _egress.GetInvoker(profile).SendAsync(request, cancellationToken);
    }
}
