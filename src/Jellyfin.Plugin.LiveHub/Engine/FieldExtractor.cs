using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LiveHub.Definitions;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Evaluates <see cref="FieldSpec"/> mappings against a JSON item.
/// </summary>
public static class FieldExtractor
{
    /// <summary>Evaluates a field.</summary>
    /// <param name="spec">The spec (null → empty).</param>
    /// <param name="item">The current item.</param>
    /// <param name="ctx">The context (its Item is replaced by <paramref name="item"/> for the evaluation).</param>
    /// <returns>The value.</returns>
    public static MappedValue Evaluate(FieldSpec? spec, JsonNode? item, TemplateContext ctx)
    {
        if (spec is null)
        {
            return MappedValue.Empty;
        }

        var local = ctx.Fork();
        local.Current = item;
        MappedValue value;
        if (spec.Value is not null)
        {
            value = MappedValue.Of(spec.Value);
        }
        else if (!string.IsNullOrEmpty(spec.Path))
        {
            var path = JsonPath.Parse(spec.Path);
            var matches = path.IsRelative || spec.Path.StartsWith('$')
                ? path.Select(item, item)
                : Array.Empty<JsonNode?>();
            if (spec.All == true)
            {
                value = new MappedValue(
                    matches.Count > 0 ? JsonPath.ToText(matches[0]) : null,
                    matches.SelectMany(Flatten).ToList(),
                    matches.Count > 0 ? matches[0] : null);
            }
            else
            {
                var first = matches.Count > 0 ? matches[0] : null;
                value = first is JsonArray arr && arr.All(n => n is JsonValue)
                    ? new MappedValue(arr.Count > 0 ? JsonPath.ToText(arr[0]) : null, arr.Select(JsonPath.ToText).ToList(), first)
                    : new MappedValue(JsonPath.ToText(first), null, first);
            }
        }
        else if (spec.Template is not null)
        {
            value = MappedValue.Of(local.Render(spec.Template));
        }
        else
        {
            value = MappedValue.Empty;
        }

        local.Value = value.Text;
        return TransformEngine.Apply(value, spec.Transforms, local);
    }

    /// <summary>Evaluates a field as trimmed text (empty → null).</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="item">The item.</param>
    /// <param name="ctx">The context.</param>
    /// <returns>The text.</returns>
    public static string? Text(FieldSpec? spec, JsonNode? item, TemplateContext ctx)
    {
        var t = Evaluate(spec, item, ctx).Text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>Evaluates a date field using its format/time zone.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="item">The item.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="defaultZone">Zone used when the spec does not declare one.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset? Date(FieldSpec? spec, JsonNode? item, TemplateContext ctx, TimeZoneInfo defaultZone)
    {
        var raw = Text(spec, item, ctx);
        if (raw is null)
        {
            return null;
        }

        var zone = DateParser.FindZone(spec?.Timezone) ?? defaultZone;
        return DateParser.Parse(raw, spec?.Format, zone);
    }

    /// <summary>Evaluates a boolean field (true/false/1/0/live/yes...).</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="item">The item.</param>
    /// <param name="ctx">The context.</param>
    /// <returns>The flag, or null when missing.</returns>
    public static bool? Bool(FieldSpec? spec, JsonNode? item, TemplateContext ctx)
    {
        var raw = Text(spec, item, ctx);
        if (raw is null)
        {
            return null;
        }

        if (bool.TryParse(raw, out var b))
        {
            return b;
        }

        return TransformEngine.DefaultTrueValues.Any(v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Evaluates a number field.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="item">The item.</param>
    /// <param name="ctx">The context.</param>
    /// <returns>The number.</returns>
    public static double? Number(FieldSpec? spec, JsonNode? item, TemplateContext ctx)
    {
        var raw = Text(spec, item, ctx);
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Evaluates a headers field: JSON object, array of {name,value}, or "Name: value" lines.</summary>
    /// <param name="spec">The spec.</param>
    /// <param name="item">The item.</param>
    /// <param name="ctx">The context.</param>
    /// <returns>The headers.</returns>
    public static Dictionary<string, string> Headers(FieldSpec? spec, JsonNode? item, TemplateContext ctx)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (spec is null)
        {
            return result;
        }

        var v = Evaluate(spec, item, ctx);
        if (v.Raw is JsonObject obj && (spec.Transforms is null || spec.Transforms.Count == 0))
        {
            foreach (var kv in obj)
            {
                var text = JsonPath.ToText(kv.Value);
                if (!string.IsNullOrEmpty(text))
                {
                    result[kv.Key] = text;
                }
            }

            return result;
        }

        if (v.Raw is JsonArray arr)
        {
            foreach (var n in arr.OfType<JsonObject>())
            {
                var name = JsonPath.ToText(n["name"] ?? n["key"] ?? n["header"]);
                var value = JsonPath.ToText(n["value"]);
                if (!string.IsNullOrEmpty(name) && value is not null)
                {
                    result[name] = value;
                }
            }

            return result;
        }

        foreach (var line in v.All().SelectMany(s => s.Split(new[] { '\n', '|' }, StringSplitOptions.RemoveEmptyEntries)))
        {
            var idx = line.IndexOf(':', StringComparison.Ordinal);
            if (idx > 0)
            {
                result[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }

        return result;
    }

    private static IEnumerable<string?> Flatten(JsonNode? node)
    {
        if (node is JsonArray a)
        {
            foreach (var n in a)
            {
                yield return JsonPath.ToText(n);
            }
        }
        else
        {
            yield return JsonPath.ToText(node);
        }
    }
}
