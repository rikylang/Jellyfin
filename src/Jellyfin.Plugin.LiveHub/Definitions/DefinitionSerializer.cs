using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LiveHub.Definitions;

/// <summary>
/// JSON (de)serialization of Provider Definitions. Unknown properties are rejected (the schema is closed).
/// </summary>
public static class DefinitionSerializer
{
    /// <summary>Gets the serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Parses a definition.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="error">The parse error, if any (with JSON path).</param>
    /// <returns>The definition or null.</returns>
    public static ProviderDefinition? TryParse(string json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty definition.";
            return null;
        }

        try
        {
            var def = JsonSerializer.Deserialize<ProviderDefinition>(json, Options);
            if (def is null)
            {
                error = "The definition is null.";
            }

            return def;
        }
        catch (JsonException ex)
        {
            error = string.IsNullOrEmpty(ex.Path) ? ex.Message : $"{ex.Path}: {ex.Message}";
            return null;
        }
        catch (NotSupportedException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Serializes a definition (indented).</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>JSON text.</returns>
    public static string Serialize(ProviderDefinition definition) => JsonSerializer.Serialize(definition, Options);

    /// <summary>Deep-clones a definition through JSON.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>A copy.</returns>
    public static ProviderDefinition Clone(ProviderDefinition definition)
        => JsonSerializer.Deserialize<ProviderDefinition>(Serialize(definition), Options)!;

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return o;
    }
}
