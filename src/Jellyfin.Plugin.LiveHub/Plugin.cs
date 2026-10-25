using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveHub.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.LiveHub;

/// <summary>
/// The Live Hub plugin: live events and 24/7 channels from any external API.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id.</summary>
    public const string PluginId = "19816826-9c4f-49a5-9222-e00484264789";

    /// <summary>The plugin display name (also used as channel name).</summary>
    public const string PluginName = "Live Hub";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Raised after the configuration has been saved.
    /// </summary>
    public event EventHandler? ConfigurationSaved;

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public override string Description => "Live events and 24/7 channels from any external API, in the Channels section and in Live TV.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace + ".Web.";
        return
        [
            new PluginPageInfo
            {
                Name = "LiveHub",
                DisplayName = PluginName,
                EmbeddedResourcePath = prefix + "admin.html"
            },
            new PluginPageInfo
            {
                Name = "LiveHub.admin.js",
                EmbeddedResourcePath = prefix + "admin.js"
            },
            new PluginPageInfo
            {
                Name = "LiveHubUser",
                DisplayName = PluginName,
                EmbeddedResourcePath = prefix + "user.html",
                EnableInMainMenu = true,
                MenuIcon = "live_tv"
            },
            new PluginPageInfo
            {
                Name = "LiveHub.user.js",
                EmbeddedResourcePath = prefix + "user.js"
            },
            new PluginPageInfo
            {
                Name = "LiveHub.common.js",
                EmbeddedResourcePath = prefix + "common.js"
            },
            new PluginPageInfo
            {
                Name = "LiveHub.css",
                EmbeddedResourcePath = prefix + "livehub.css"
            }
        ];
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        ConfigurationSaved?.Invoke(this, EventArgs.Empty);
    }
}
