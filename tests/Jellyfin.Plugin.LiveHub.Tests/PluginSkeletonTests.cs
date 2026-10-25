using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Localization;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests;

public class PluginSkeletonTests
{
    [Fact]
    public void EmbeddedWebPagesAndStringsExist()
    {
        var asm = typeof(Plugin).Assembly;
        var names = asm.GetManifestResourceNames();
        foreach (var file in new[] { "admin.html", "admin.js", "user.html", "user.js", "common.js", "livehub.css" })
        {
            Assert.Contains("Jellyfin.Plugin.LiveHub.Web." + file, names);
        }

        Assert.Contains("Jellyfin.Plugin.LiveHub.Localization.strings.it.json", names);
        Assert.Contains("Jellyfin.Plugin.LiveHub.Localization.strings.en.json", names);
        Assert.Contains("Jellyfin.Plugin.LiveHub.Definitions.provider.schema.json", names);
    }

    [Fact]
    public void ItalianAndEnglishHaveTheSameKeys()
    {
        var loc = new LocalizationService();
        var en = loc.GetStrings("en").Keys.ToHashSet();
        var it = loc.GetStrings("it").Keys.ToHashSet();
        Assert.Empty(en.Except(it));
        Assert.Equal("In diretta ora", loc.Get("it-IT", "folder.live"));
        Assert.Equal("Live now", loc.Get("de", "folder.live"));
    }

    [Fact]
    public void EveryUiStringKeyUsedByThePagesExists()
    {
        var asm = typeof(Plugin).Assembly;
        var keys = new LocalizationService().GetStrings("en").Keys.ToHashSet();
        var missing = new System.Collections.Generic.List<string>();
        foreach (var file in new[] { "admin.js", "user.js", "admin.html", "user.html" })
        {
            using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.LiveHub.Web." + file)!;
            var text = new StreamReader(stream).ReadToEnd();
            foreach (Match m in Regex.Matches(text, @"\bt\('([A-Za-z0-9_.]+)'\)|data-i18n(?:-placeholder|-title)?=""([A-Za-z0-9_.]+)"""))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!keys.Contains(key))
                {
                    missing.Add(file + ": " + key);
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void BuildYamlMatchesPluginIdAndAbi()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "build.yaml"));
        Assert.Contains("guid: \"" + Plugin.PluginId + "\"", yaml, StringComparison.Ordinal);
        Assert.Matches(new Regex("targetAbi: \"12\\.3\\.0\\.0\""), yaml);
        Assert.Contains("framework: \"net10.0\"", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationDefaultsAreAppliedOnceWithoutDuplicates()
    {
        var cfg = new PluginConfiguration();
        Assert.True(cfg.EnsureDefaults());
        var count = cfg.CategoryDurations.Count;
        Assert.False(cfg.EnsureDefaults());
        Assert.Equal(count, cfg.CategoryDurations.Count);
        Assert.Single(cfg.EgressProfiles, p => p.Id == EgressProfile.DirectId);
    }
}
