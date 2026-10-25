using System.Collections.Generic;
using Jellyfin.Plugin.LiveHub.Configuration;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>Result of "verify connection" for an egress profile.</summary>
public sealed class EgressCheckResult
{
    /// <summary>Gets or sets the profile id.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Gets or sets the route description (credentials masked).</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the profile works.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the public IP seen by the outside world.</summary>
    public string? Ip { get; set; }

    /// <summary>Gets or sets the exit country (ISO 3166-1 alpha-2).</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the city.</summary>
    public string? City { get; set; }

    /// <summary>Gets or sets the network operator.</summary>
    public string? Org { get; set; }

    /// <summary>Gets or sets the expected country.</summary>
    public string? Expected { get; set; }

    /// <summary>Gets or sets a value indicating whether the exit country matches the expected one.</summary>
    public bool CountryMatches { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the proxy exits with the server's own IP (VPN not active).</summary>
    public bool SameAsDirect { get; set; }

    /// <summary>Gets or sets the latency.</summary>
    public double LatencyMs { get; set; }

    /// <summary>Gets or sets the error.</summary>
    public string? Error { get; set; }
}

/// <summary>Status reported by a gluetun control server.</summary>
public sealed class GluetunStatus
{
    /// <summary>Gets or sets the VPN status (running, stopped...).</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the public IP of the tunnel.</summary>
    public string? PublicIp { get; set; }

    /// <summary>Gets or sets the country of the tunnel exit.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the region.</summary>
    public string? Region { get; set; }

    /// <summary>Gets or sets the city.</summary>
    public string? City { get; set; }

    /// <summary>Gets or sets the organization.</summary>
    public string? Organization { get; set; }

    /// <summary>Gets or sets a value indicating whether the server country can be changed at runtime.</summary>
    public bool CanChangeCountry { get; set; }

    /// <summary>Gets or sets the instructions when it cannot.</summary>
    public string? Instructions { get; set; }

    /// <summary>Gets or sets the error.</summary>
    public string? Error { get; set; }
}

/// <summary>Body of POST Admin/Egress.</summary>
public sealed class EgressSaveRequest
{
    /// <summary>Gets or sets the profiles.</summary>
    public List<EgressProfile> Profiles { get; set; } = new();

    /// <summary>Gets or sets the default profile.</summary>
    public string? DefaultProfileId { get; set; }

    /// <summary>Gets or sets the region rules.</summary>
    public List<RegionEgressRule> RegionEgress { get; set; } = new();
}
