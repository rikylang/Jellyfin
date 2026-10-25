using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.LiveHub.Definitions;

/// <summary>A template of the library.</summary>
public sealed class DefinitionTemplate
{
    /// <summary>Gets or sets the file name.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the JSON text.</summary>
    public string Json { get; set; } = string.Empty;
}

/// <summary>
/// Built-in templates (generic REST JSON, M3U+XMLTV, Xtream Codes, direct stream) embedded in the plugin, plus the schema.
/// </summary>
public static class TemplateLibrary
{
    private static readonly Lazy<IReadOnlyList<DefinitionTemplate>> Templates = new(Load);

    /// <summary>Gets the templates.</summary>
    /// <returns>The templates.</returns>
    public static IReadOnlyList<DefinitionTemplate> All() => Templates.Value;

    /// <summary>Gets the provider schema text.</summary>
    /// <returns>The JSON schema.</returns>
    public static string Schema()
    {
        using var stream = typeof(TemplateLibrary).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LiveHub.Definitions.provider.schema.json")
            ?? throw new InvalidOperationException("Embedded schema missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<DefinitionTemplate> Load()
    {
        var asm = typeof(TemplateLibrary).Assembly;
        var list = new List<DefinitionTemplate>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var def = DefinitionSerializer.TryParse(json, out _);
            if (def is null)
            {
                continue;
            }

            list.Add(new DefinitionTemplate
            {
                File = name[(name.IndexOf(".Templates.", StringComparison.Ordinal) + 11)..],
                Name = def.Name,
                Description = def.Description,
                Kind = def.Kind.ToString().ToLowerInvariant(),
                Json = json,
            });
        }

        return list.OrderBy(t => t.Kind == "rest" ? 0 : t.Kind == "m3u" ? 1 : t.Kind == "xtream" ? 2 : 3).ToList();
    }
}
