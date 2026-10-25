using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Variables and current JSON item used to render templates such as "{baseUrl}/events/{event.id}?d={date:yyyyMMdd}".
/// </summary>
public sealed class TemplateContext
{
    private readonly Dictionary<string, string?> _vars;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateContext"/> class.
    /// </summary>
    /// <param name="clock">Clock for {now}/{date}.</param>
    /// <param name="timeZone">Time zone for {date}.</param>
    public TemplateContext(TimeProvider clock, TimeZoneInfo timeZone)
    {
        Clock = clock;
        TimeZone = timeZone;
        _vars = new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    private TemplateContext(TemplateContext parent)
    {
        Clock = parent.Clock;
        TimeZone = parent.TimeZone;
        _vars = new Dictionary<string, string?>(parent._vars, StringComparer.Ordinal);
        Root = parent.Root;
        Current = parent.Current;
        Value = parent.Value;
        Missing = parent.Missing;
    }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Gets the time zone used for {date}.</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Gets or sets the document root ($).</summary>
    public JsonNode? Root { get; set; }

    /// <summary>Gets or sets the current item (@ and {$.x} in templates are relative to it).</summary>
    public JsonNode? Current { get; set; }

    /// <summary>Gets or sets the current value ({value}) inside transforms.</summary>
    public string? Value { get; set; }

    /// <summary>Gets the collection of missing variable names (diagnostics), when tracking is enabled.</summary>
    public ICollection<string>? Missing { get; private set; }

    /// <summary>Gets or sets a variable.</summary>
    /// <param name="name">Name such as "baseUrl" or "event.id".</param>
    public string? this[string name]
    {
        get => _vars.TryGetValue(name, out var v) ? v : null;
        set => _vars[name] = value;
    }

    /// <summary>Enables tracking of unresolved variables (shared with forks).</summary>
    /// <returns>The collection receiving missing names.</returns>
    public ICollection<string> TrackMissing()
    {
        Missing ??= new List<string>();
        return Missing;
    }

    /// <summary>Creates a child context sharing variables (copy-on-write).</summary>
    /// <returns>The child.</returns>
    public TemplateContext Fork() => new(this);

    /// <summary>Sets all entries of a dictionary under a prefix ("event." + key).</summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="values">The values.</param>
    public void SetAll(string prefix, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var kv in values)
        {
            _vars[prefix + kv.Key] = kv.Value;
        }
    }

    /// <summary>Renders a template.</summary>
    /// <param name="template">The template.</param>
    /// <returns>The rendered text.</returns>
    public string Render(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        if (template.IndexOf('{', StringComparison.Ordinal) < 0 && template.IndexOf('}', StringComparison.Ordinal) < 0)
        {
            return template;
        }

        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sb.Append('{');
                    i += 2;
                    continue;
                }

                var end = FindClose(template, i + 1);
                if (end < 0)
                {
                    sb.Append(template, i, template.Length - i);
                    break;
                }

                sb.Append(Resolve(template.Substring(i + 1, end - i - 1)));
                i = end + 1;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int FindClose(string s, int start)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '[' || c == '(')
            {
                depth++;
            }
            else if (c == ']' || c == ')')
            {
                depth--;
            }
            else if (c == '}' && depth <= 0)
            {
                return i;
            }
        }

        return -1;
    }

    private string Resolve(string expression)
    {
        var expr = expression.Trim();
        string? filter = null;
        var pipe = LastTopLevelPipe(expr);
        if (pipe > 0)
        {
            filter = expr[(pipe + 1)..].Trim();
            expr = expr[..pipe].Trim();
        }

        string? value;
        if (expr.StartsWith('$') || expr.StartsWith('@'))
        {
            try
            {
                var path = JsonPath.Parse(expr);
                // In templates "$" is the current item (same convention as field paths); the document root is used
                // only when there is no current item (e.g. pagination cursors).
                value = JsonPath.ToText(path.SelectFirst(Current ?? Root, Current));
            }
            catch (FormatException)
            {
                value = null;
            }
        }
        else if (string.Equals(expr, "value", StringComparison.Ordinal))
        {
            value = Value;
        }
        else if (string.Equals(expr, "now", StringComparison.Ordinal))
        {
            value = Clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
        else if (string.Equals(expr, "nowMs", StringComparison.Ordinal))
        {
            value = Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
        else if (expr.StartsWith("date", StringComparison.Ordinal) && (expr.Length == 4 || expr[4] == ':'))
        {
            var format = expr.Length > 5 ? expr[5..] : "yyyy-MM-dd";
            if (expr.Length == 4 && this["date.value"] is { } iterated)
            {
                return ApplyFilter(iterated, filter);
            }

            var dateVar = this["date.iso"];
            var date = dateVar is not null && DateTime.TryParse(dateVar, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dv)
                ? dv
                : TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), TimeZone).DateTime;
            value = date.ToString(format, CultureInfo.InvariantCulture);
        }
        else if (_vars.TryGetValue(expr, out var v))
        {
            value = v;
        }
        else
        {
            value = null;
            Missing?.Add(expr);
        }

        return ApplyFilter(value ?? string.Empty, filter);
    }

    private static int LastTopLevelPipe(string expr)
    {
        var depth = 0;
        var quote = '\0';
        var found = -1;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '[' or '(': depth++; break;
                case ']' or ')': depth--; break;
                case '|' when depth == 0 && (i + 1 >= expr.Length || expr[i + 1] != '|') && (i == 0 || expr[i - 1] != '|'):
                    found = i;
                    break;
            }
        }

        return found;
    }

    private static string ApplyFilter(string value, string? filter)
    {
        return filter switch
        {
            null or "" => value,
            "urlencode" => Uri.EscapeDataString(value),
            "lower" => value.ToLowerInvariant(),
            "upper" => value.ToUpperInvariant(),
            "trim" => value.Trim(),
            "base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
            _ => value,
        };
    }
}
