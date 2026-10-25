namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>Resolves the egress profile of a provider.</summary>
public interface IEgressResolver
{
    /// <summary>Resolves the profile id.</summary>
    /// <param name="providerProfile">Explicit profile.</param>
    /// <param name="providerCountry">Provider default country.</param>
    /// <returns>The profile id.</returns>
    string ResolveProfileId(string? providerProfile, string? providerCountry);
}
