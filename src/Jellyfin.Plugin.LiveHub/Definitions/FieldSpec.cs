using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LiveHub.Definitions;

/// <summary>
/// A field mapping: JSONPath ("$.title"), template ("{$.home} vs {$.away}"), constant, or an object with
/// path/template/value + transforms + date format/time zone.
/// </summary>
[JsonConverter(typeof(FieldSpecConverter))]
public sealed class FieldSpec
{
    /// <summary>Gets or sets the JSONPath.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the template.</summary>
    public string? Template { get; set; }

    /// <summary>Gets or sets the constant value.</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets the transforms.</summary>
    public List<TransformSpec>? Transforms { get; set; }

    /// <summary>Gets or sets the date format.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the time zone.</summary>
    public string? Timezone { get; set; }

    /// <summary>Gets or sets a value indicating whether all matches are collected.</summary>
    public bool? All { get; set; }

    /// <summary>Gets or sets a value indicating whether the spec came from a shorthand string (kept when serializing).</summary>
    [JsonIgnore]
    public bool IsShorthand { get; set; }

    /// <summary>
    /// Parses a shorthand string: "$..."/"@..." → path, text containing "{" → template, anything else → constant.
    /// </summary>
    /// <param name="text">The shorthand.</param>
    /// <returns>The spec.</returns>
    public static FieldSpec FromShorthand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (t.StartsWith('$') || t.StartsWith('@'))
        {
            return new FieldSpec { Path = t, IsShorthand = true };
        }

        if (t.Contains('{', StringComparison.Ordinal))
        {
            return new FieldSpec { Template = text, IsShorthand = true };
        }

        return new FieldSpec { Value = text, IsShorthand = true };
    }

    /// <summary>Creates a path spec.</summary>
    /// <param name="path">The JSONPath.</param>
    /// <returns>The spec.</returns>
    public static FieldSpec OfPath(string path) => new() { Path = path, IsShorthand = true };
}

/// <summary>A declarative transform.</summary>
public sealed class TransformSpec
{
    /// <summary>Gets or sets the operation.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>Gets or sets the value parameter.</summary>
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Value { get; set; }

    /// <summary>Gets or sets the values parameter.</summary>
    public List<string>? Values { get; set; }

    /// <summary>Gets or sets the lookup table.</summary>
    public Dictionary<string, string?>? Table { get; set; }

    /// <summary>Gets or sets the default for map.</summary>
    public string? Default { get; set; }

    /// <summary>Gets or sets a value indicating whether lookups ignore case.</summary>
    public bool? IgnoreCase { get; set; }

    /// <summary>Gets or sets a value indicating whether unknown map keys become null.</summary>
    public bool? Strict { get; set; }

    /// <summary>Gets or sets the regex pattern.</summary>
    public string? Pattern { get; set; }

    /// <summary>Gets or sets the regex replacement.</summary>
    public string? Replacement { get; set; }

    /// <summary>Gets or sets the regex group (number or name).</summary>
    [JsonConverter(typeof(LooseStringConverter))]
    public string? Group { get; set; }

    /// <summary>Gets or sets the separator.</summary>
    public string? Separator { get; set; }

    /// <summary>Gets or sets the index.</summary>
    public int? Index { get; set; }

    /// <summary>Gets or sets the base URL.</summary>
    public string? Base { get; set; }

    /// <summary>Gets or sets the values considered true.</summary>
    public List<string>? TrueValues { get; set; }
}

/// <summary>Field mapping of an endpoint: field name → spec, plus the structured participants mapping.</summary>
[JsonConverter(typeof(FieldMapConverter))]
public sealed class FieldMap : Dictionary<string, FieldSpec>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FieldMap"/> class.
    /// </summary>
    public FieldMap()
        : base(StringComparer.Ordinal)
    {
    }

    /// <summary>Gets or sets the participants mapping.</summary>
    public ParticipantsSpec? Participants { get; set; }
}

/// <summary>Reads numbers and booleans as strings.</summary>
public sealed class LooseStringConverter : JsonConverter<string?>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException("Expected a string, number or boolean."),
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

/// <summary>Converter for <see cref="FieldSpec"/> (string shorthand or object).</summary>
public sealed class FieldSpecConverter : JsonConverter<FieldSpec>
{
    /// <inheritdoc />
    public override FieldSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return FieldSpec.FromShorthand(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A field must be a string or an object.");
        }

        var spec = new FieldSpec();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return spec;
            }

            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "path": spec.Path = reader.GetString(); break;
                case "template": spec.Template = reader.GetString(); break;
                case "value": spec.Value = new LooseStringConverter().Read(ref reader, typeof(string), options); break;
                case "transforms": spec.Transforms = JsonSerializer.Deserialize<List<TransformSpec>>(ref reader, options); break;
                case "format": spec.Format = reader.GetString(); break;
                case "timezone": spec.Timezone = reader.GetString(); break;
                case "all": spec.All = reader.GetBoolean(); break;
                default: throw new JsonException($"Unknown field property '{name}'.");
            }
        }

        throw new JsonException("Unterminated field object.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FieldSpec value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        var simple = value.Transforms is null && value.Format is null && value.Timezone is null && value.All is null;
        if (simple && value.IsShorthand)
        {
            if (value.Path is not null)
            {
                writer.WriteStringValue(value.Path);
                return;
            }

            if (value.Template is not null)
            {
                writer.WriteStringValue(value.Template);
                return;
            }

            if (value.Value is not null && !value.Value.StartsWith('$') && !value.Value.StartsWith('@') && !value.Value.Contains('{', StringComparison.Ordinal))
            {
                writer.WriteStringValue(value.Value);
                return;
            }
        }

        writer.WriteStartObject();
        if (value.Path is not null)
        {
            writer.WriteString("path", value.Path);
        }

        if (value.Template is not null)
        {
            writer.WriteString("template", value.Template);
        }

        if (value.Value is not null)
        {
            writer.WriteString("value", value.Value);
        }

        if (value.Transforms is not null)
        {
            writer.WritePropertyName("transforms");
            JsonSerializer.Serialize(writer, value.Transforms, options);
        }

        if (value.Format is not null)
        {
            writer.WriteString("format", value.Format);
        }

        if (value.Timezone is not null)
        {
            writer.WriteString("timezone", value.Timezone);
        }

        if (value.All is not null)
        {
            writer.WriteBoolean("all", value.All.Value);
        }

        writer.WriteEndObject();
    }
}

/// <summary>Converter for <see cref="FieldMap"/>.</summary>
public sealed class FieldMapConverter : JsonConverter<FieldMap>
{
    /// <inheritdoc />
    public override FieldMap? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("'fields' must be an object.");
        }

        var map = new FieldMap();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return map;
            }

            var name = reader.GetString() ?? string.Empty;
            reader.Read();
            if (string.Equals(name, "participants", StringComparison.Ordinal))
            {
                map.Participants = JsonSerializer.Deserialize<ParticipantsSpec>(ref reader, options);
            }
            else
            {
                map[name] = JsonSerializer.Deserialize<FieldSpec>(ref reader, options) ?? new FieldSpec();
            }
        }

        throw new JsonException("Unterminated 'fields' object.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FieldMap value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            writer.WritePropertyName(kv.Key);
            JsonSerializer.Serialize(writer, kv.Value, options);
        }

        if (value.Participants is not null)
        {
            writer.WritePropertyName("participants");
            JsonSerializer.Serialize(writer, value.Participants, options);
        }

        writer.WriteEndObject();
    }
}
