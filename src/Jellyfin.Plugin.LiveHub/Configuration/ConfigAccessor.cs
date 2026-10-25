using System;

namespace Jellyfin.Plugin.LiveHub.Configuration;

/// <summary>
/// Gives services access to the current plugin configuration (testable indirection over <see cref="Plugin.Instance"/>).
/// </summary>
public class ConfigAccessor
{
    private readonly Func<PluginConfiguration> _get;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAccessor"/> class reading <see cref="Plugin.Instance"/>.
    /// </summary>
    public ConfigAccessor()
        : this(() => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigAccessor"/> class.
    /// </summary>
    /// <param name="get">Configuration getter.</param>
    public ConfigAccessor(Func<PluginConfiguration> get)
    {
        _get = get;
    }

    /// <summary>Gets the current configuration.</summary>
    public PluginConfiguration Current => _get();

    /// <summary>Saves the configuration (no-op outside Jellyfin).</summary>
    public virtual void Save()
    {
        Plugin.Instance?.SaveConfiguration();
    }
}
