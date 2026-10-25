using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Definitions;
using YamlDotNet.Serialization;

namespace Jellyfin.Plugin.LiveHub.Wizard;

/// <summary>
/// Reads OpenAPI 3 / Swagger 2 specifications (JSON or YAML) and lists the operations, with a suggested role for each.
/// </summary>
public static class OpenApiParser
{
    /// <summary>Parses a specification.</summary>
    /// <param name="text">JSON or YAML text.</param>
    /// <returns>The parsed spec, or null when the text is not a specification.</returns>
    public static OpenApiDocument? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        JsonNode? root;
        var trimmed = text.TrimStart();
        try
        {
            root = trimmed.StartsWith('{') ? JsonNode.Parse(trimmed) : YamlToJson(trimmed);
        }
        catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException or InvalidOperationException)
        {
            return null;
        }

        if (root is not JsonObject obj || (obj["openapi"] is null && obj["swagger"] is null))
        {
            return null;
        }

        var doc = new OpenApiDocument
        {
            Title = Engine.JsonPath.ToText(obj["info"]?["title"]),
        };

        // Base URL: OpenAPI 3 servers[0].url, Swagger 2 schemes + host + basePath.
        if (obj["servers"] is JsonArray servers && servers.Count > 0)
        {
            doc.BaseUrl = Engine.JsonPath.ToText(servers[0]?["url"]);
        }
        else if (obj["host"] is not null)
        {
            var scheme = (obj["schemes"] as JsonArray)?.Select(Engine.JsonPath.ToText).FirstOrDefault() ?? "https";
            doc.BaseUrl = $"{scheme}://{Engine.JsonPath.ToText(obj["host"])}{Engine.JsonPath.ToText(obj["basePath"])}";
        }

        // API key security scheme.
        var schemes = obj["components"]?["securitySchemes"] as JsonObject ?? obj["securityDefinitions"] as JsonObject;
        foreach (var kv in schemes ?? new JsonObject())
        {
            var type = Engine.JsonPath.ToText(kv.Value?["type"]);
            if (string.Equals(type, "apiKey", StringComparison.OrdinalIgnoreCase))
            {
                doc.Auth = new AuthSpec
                {
                    Type = AuthType.ApiKey,
                    In = string.Equals(Engine.JsonPath.ToText(kv.Value?["in"]), "query", StringComparison.OrdinalIgnoreCase) ? KeyLocation.Query : KeyLocation.Header,
                    Name = Engine.JsonPath.ToText(kv.Value?["name"]),
                    Value = "{secret.apiKey}",
                };
                break;
            }

            var schemeName = Engine.JsonPath.ToText(kv.Value?["scheme"]);
            if (string.Equals(type, "http", StringComparison.OrdinalIgnoreCase) && string.Equals(schemeName, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                doc.Auth = new AuthSpec { Type = AuthType.Bearer, Value = "{secret.token}" };
                break;
            }

            if (string.Equals(type, "oauth2", StringComparison.OrdinalIgnoreCase))
            {
                var tokenUrl = Engine.JsonPath.ToText(kv.Value?["flows"]?["clientCredentials"]?["tokenUrl"]) ?? Engine.JsonPath.ToText(kv.Value?["tokenUrl"]);
                if (tokenUrl is not null)
                {
                    doc.Auth = new AuthSpec { Type = AuthType.Oauth2ClientCredentials, TokenUrl = tokenUrl, ClientId = "{secret.clientId}", ClientSecret = "{secret.clientSecret}" };
                    break;
                }
            }
        }

        foreach (var path in (obj["paths"] as JsonObject) ?? new JsonObject())
        {
            if (path.Value is not JsonObject methods)
            {
                continue;
            }

            foreach (var m in methods)
            {
                var method = m.Key.ToUpperInvariant();
                if (method is not ("GET" or "POST") || m.Value is not JsonObject op)
                {
                    continue;
                }

                var operation = new OpenApiOperation
                {
                    Method = method,
                    Path = path.Key,
                    OperationId = Engine.JsonPath.ToText(op["operationId"]),
                    Summary = Engine.JsonPath.ToText(op["summary"]) ?? Engine.JsonPath.ToText(op["description"]),
                };
                foreach (var p in (op["parameters"] as JsonArray ?? new JsonArray()).Concat(methods["parameters"] as JsonArray ?? new JsonArray()))
                {
                    if (string.Equals(Engine.JsonPath.ToText(p?["in"]), "query", StringComparison.OrdinalIgnoreCase) && Engine.JsonPath.ToText(p?["name"]) is { } name)
                    {
                        operation.QueryParameters.Add(name);
                    }
                }

                operation.SuggestedRole = SuggestRole(operation);
                doc.Operations.Add(operation);
            }
        }

        return doc;
    }

    /// <summary>Converts an operation path to a URL template ({id} → {event.id} for detail/stream endpoints).</summary>
    /// <param name="op">The operation.</param>
    /// <returns>The template (relative to {baseUrl}).</returns>
    public static string ToUrlTemplate(OpenApiOperation op)
    {
        ArgumentNullException.ThrowIfNull(op);
        static string Replace(Match m)
        {
            var name = m.Groups[1].Value;
            return name.Contains("source", StringComparison.OrdinalIgnoreCase) ? "{source.id}"
                : name.Contains("categ", StringComparison.OrdinalIgnoreCase) || name.Contains("sport", StringComparison.OrdinalIgnoreCase) ? "{category.id}"
                : "{event.id}";
        }

        var path = Regex.Replace(op.Path, "\\{([^}]+)\\}", Replace, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return "{baseUrl}" + path;
    }

    private static string? SuggestRole(OpenApiOperation op)
    {
        var text = (op.Path + " " + op.OperationId + " " + op.Summary).ToLowerInvariant();
        var hasPathParam = op.Path.Contains('{', StringComparison.Ordinal);
        if (Regex.IsMatch(text, "stream|source|link|feed|play|watch", RegexOptions.None, TimeSpan.FromMilliseconds(50)) && hasPathParam)
        {
            return "streams";
        }

        if (Regex.IsMatch(text, "categor|sports?\\b|genre|leagues?", RegexOptions.None, TimeSpan.FromMilliseconds(50)) && !hasPathParam)
        {
            return "categories";
        }

        if (Regex.IsMatch(text, "channel", RegexOptions.None, TimeSpan.FromMilliseconds(50)) && !hasPathParam)
        {
            return "channels";
        }

        if (Regex.IsMatch(text, "event|match|fixture|schedule|game|live|program|broadcast", RegexOptions.None, TimeSpan.FromMilliseconds(50)))
        {
            return hasPathParam ? "eventDetail" : "events";
        }

        return null;
    }

    private static JsonNode? YamlToJson(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var obj = deserializer.Deserialize<object?>(yaml);
        return ToNode(obj, 0);
    }

    private static JsonNode? ToNode(object? value, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidOperationException("YAML too deep.");
        }

        return value switch
        {
            null => null,
            IDictionary<object, object?> map => new JsonObject(map.Select(kv => KeyValuePair.Create(Convert.ToString(kv.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, ToNode(kv.Value, depth + 1)))),
            IList<object?> list => new JsonArray(list.Select(v => ToNode(v, depth + 1)).ToArray()),
            string s => JsonValue.Create(s),
            _ => JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)),
        };
    }
}
