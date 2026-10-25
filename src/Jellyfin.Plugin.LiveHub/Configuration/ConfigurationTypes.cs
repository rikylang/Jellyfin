namespace Jellyfin.Plugin.LiveHub.Configuration;

/// <summary>Egress profile type.</summary>
public enum EgressType
{
    /// <summary>Direct connection from the Jellyfin host.</summary>
    Direct = 0,

    /// <summary>HTTP proxy (CONNECT for https).</summary>
    Http = 1,

    /// <summary>HTTPS proxy (TLS to the proxy).</summary>
    Https = 2,

    /// <summary>SOCKS5 proxy, DNS resolved by the proxy.</summary>
    Socks5 = 3,
}

/// <summary>
/// An outgoing connection profile (direct, HTTP(S) proxy or SOCKS5, e.g. the gluetun container).
/// </summary>
public class EgressProfile
{
    /// <summary>Id of the built-in direct profile.</summary>
    public const string DirectId = "direct";

    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the type.</summary>
    public EgressType Type { get; set; }

    /// <summary>Gets or sets the proxy host.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the proxy port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the proxy user name.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the proxy password (encrypted at rest).</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected exit country (ISO 3166-1 alpha-2), checked by "verify connection".</summary>
    public string ExpectedCountry { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether requests are blocked while the profile is unhealthy.</summary>
    public bool KillSwitch { get; set; } = true;

    /// <summary>Gets or sets the health check interval, in seconds.</summary>
    public int HealthCheckSeconds { get; set; } = 60;

    /// <summary>Gets or sets the gluetun control server URL associated to this profile (optional).</summary>
    public string GluetunControlUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the gluetun control server API key (encrypted at rest).</summary>
    public string GluetunApiKey { get; set; } = string.Empty;

    /// <summary>Creates the built-in direct profile.</summary>
    /// <returns>The profile.</returns>
    public static EgressProfile CreateDirect() => new() { Id = DirectId, Name = "Diretto / Direct", Type = EgressType.Direct, KillSwitch = false };
}

/// <summary>Estimated duration for events of matching categories.</summary>
public class CategoryDurationRule
{
    /// <summary>Gets or sets the regex matched against category and competition.</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Gets or sets the duration in minutes.</summary>
    public int Minutes { get; set; } = 120;
}

/// <summary>Maps events to a country (region filters).</summary>
public class RegionRule
{
    /// <summary>Gets or sets the regex matched against category, competition and title.</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Gets or sets the country (ISO 3166-1 alpha-2).</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the language (ISO 639-1), optional.</summary>
    public string Language { get; set; } = string.Empty;
}

/// <summary>Assigns an egress profile to all providers of a region.</summary>
public class RegionEgressRule
{
    /// <summary>Gets or sets the country (ISO 3166-1 alpha-2).</summary>
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the egress profile id.</summary>
    public string ProfileId { get; set; } = string.Empty;
}

/// <summary>Restricts the providers a user can see.</summary>
public class UserAccessRule
{
    /// <summary>Gets or sets the Jellyfin user id ("N" format).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is restricted to <see cref="AllowedProviders"/>.</summary>
    public bool Restricted { get; set; }

    /// <summary>Gets or sets the comma separated allowed provider ids.</summary>
    public string AllowedProviders { get; set; } = string.Empty;
}
