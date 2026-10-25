using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Engine;

namespace Jellyfin.Plugin.LiveHub.Definitions;

/// <summary>A validation issue with its JSON path.</summary>
public sealed class ValidationIssue
{
    /// <summary>Gets or sets the JSON path (events[0].fields.start).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : Path + ": " + Message;
}

/// <summary>The result of a validation.</summary>
public sealed class ValidationResult
{
    /// <summary>Gets the errors.</summary>
    public List<ValidationIssue> Errors { get; } = new();

    /// <summary>Gets the warnings.</summary>
    public List<ValidationIssue> Warnings { get; } = new();

    /// <summary>Gets a value indicating whether there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Semantic validation of a definition (mirrors provider.schema.json and adds checks a schema cannot express:
/// regexes compile with a timeout, JSONPaths parse, URLs render to absolute http(s), transforms have their parameters).
/// </summary>
public static class DefinitionValidator
{
    private static readonly Regex IdRegex = new("^[a-z0-9][a-z0-9_-]{1,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> EventFields = new(StringComparer.Ordinal)
    {
        "id", "title", "name", "category", "categoryId", "competition", "start", "end", "durationMinutes", "isLive", "status",
        "popularity", "poster", "thumb", "logo", "image", "homeTeam", "awayTeam", "homeLogo", "awayLogo", "country", "language",
        "description", "pageUrl", "tags", "contentRating", "number", "epgId",
    };

    private static readonly HashSet<string> StreamFields = new(StringComparer.Ordinal)
    {
        "id", "url", "type", "quality", "height", "bitrate", "hd", "language", "country", "headers", "referer", "userAgent", "expiresAt", "label",
    };

    /// <summary>Validates a definition.</summary>
    /// <param name="def">The definition.</param>
    /// <returns>The result.</returns>
    public static ValidationResult Validate(ProviderDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        var r = new ValidationResult();
        void Error(string path, string message) => r.Errors.Add(new ValidationIssue { Path = path, Message = message });
        void Warn(string path, string message) => r.Warnings.Add(new ValidationIssue { Path = path, Message = message });

        if (def.SchemaVersion != 1)
        {
            Error("schemaVersion", "Only schema version 1 is supported.");
        }

        if (string.IsNullOrEmpty(def.Id) || !IdRegex.IsMatch(def.Id))
        {
            Error("id", "Use 2-64 characters: lowercase letters, digits, '-' and '_', starting with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(def.Name) || def.Name.Length > 120)
        {
            Error("name", "The name is required (max 120 characters).");
        }

        if (def.Priority is < 0 or > 1000)
        {
            Error("priority", "Must be between 0 and 1000.");
        }

        var conn = def.Connection;
        if (conn is not null)
        {
            if (conn.TimeoutSeconds is < 2 or > 120)
            {
                Error("connection.timeoutSeconds", "Must be between 2 and 120.");
            }

            if (conn.MaxResponseBytes is < 1024 or > 67_108_864)
            {
                Error("connection.maxResponseBytes", "Must be between 1 KB and 64 MB.");
            }

            if (conn.RateLimit is { } rl && (rl.RequestsPerMinute is < 1 or > 6000 || rl.Burst is < 1 or > 100))
            {
                Error("connection.rateLimit", "requestsPerMinute 1-6000, burst 1-100.");
            }

            if (conn.Auth is { } auth)
            {
                switch (auth.Type)
                {
                    case AuthType.ApiKey when string.IsNullOrWhiteSpace(auth.Value):
                    case AuthType.Bearer when string.IsNullOrWhiteSpace(auth.Value):
                    case AuthType.Cookie when string.IsNullOrWhiteSpace(auth.Value):
                        Error("connection.auth.value", "A value is required for this authentication type.");
                        break;
                    case AuthType.Basic when string.IsNullOrWhiteSpace(auth.Username):
                        Error("connection.auth.username", "A user name is required for basic authentication.");
                        break;
                    case AuthType.Oauth2ClientCredentials when string.IsNullOrWhiteSpace(auth.TokenUrl) || string.IsNullOrWhiteSpace(auth.ClientId):
                        Error("connection.auth", "tokenUrl and clientId are required for OAuth2 client credentials.");
                        break;
                }

                foreach (var (path, value) in new[] { ("connection.auth.value", auth.Value), ("connection.auth.password", auth.Password), ("connection.auth.clientSecret", auth.ClientSecret) })
                {
                    if (!string.IsNullOrEmpty(value) && !value.Contains("{secret.", StringComparison.Ordinal) && value.Length >= 6)
                    {
                        Warn(path, "Put credentials in 'secrets' and reference them as {secret.name}: inline values are not encrypted.");
                    }
                }
            }
        }

        var vars = new TemplateContext(TimeProvider.System, TimeZoneInfo.Utc);
        foreach (var kv in def.Variables ?? new Dictionary<string, string>())
        {
            vars["var." + kv.Key] = kv.Value;
        }

        foreach (var kv in def.Secrets ?? new Dictionary<string, string>())
        {
            vars["secret." + kv.Key] = "secret";
        }

        var baseUrl = vars.Render(conn?.BaseUrl);
        if (!string.IsNullOrWhiteSpace(conn?.BaseUrl) && !IsHttpUrl(baseUrl))
        {
            Error("connection.baseUrl", "Must be an absolute http(s) URL.");
        }

        vars["baseUrl"] = string.IsNullOrWhiteSpace(baseUrl) ? "https://example.invalid" : baseUrl.TrimEnd('/');
        foreach (var dummy in new[] { "page", "offset", "limit", "cursor", "category.id", "category.name", "category", "event.id", "date", "source.id" })
        {
            vars[dummy] = "x";
        }

        switch (def.Kind)
        {
            case ProviderKind.Rest:
                if ((def.Events is null || def.Events.Count == 0) && (def.Channels is null || def.Channels.Count == 0))
                {
                    Error("events", "A REST provider needs at least one 'events' or 'channels' endpoint.");
                }

                if (def.Categories is not null)
                {
                    ValidateEndpoint("categories", def.Categories, "categories", vars, Error, Warn);
                }

                for (var i = 0; i < (def.Events?.Count ?? 0); i++)
                {
                    ValidateEndpoint($"events[{i}]", def.Events![i], "events", vars, Error, Warn);
                    if (def.Events[i].ForEach == ForEachMode.Category && def.Categories is null)
                    {
                        Error($"events[{i}].forEach", "forEach 'category' requires a 'categories' endpoint.");
                    }
                }

                for (var i = 0; i < (def.Channels?.Count ?? 0); i++)
                {
                    ValidateEndpoint($"channels[{i}]", def.Channels![i], "channels", vars, Error, Warn);
                }

                if (def.EventDetail is not null)
                {
                    ValidateEndpoint("eventDetail", def.EventDetail, "detail", vars, Error, Warn);
                }

                if (def.Streams is { } chain)
                {
                    if (chain.Levels.Count is < 1 or > 4)
                    {
                        Error("streams.levels", "Between 1 and 4 levels.");
                    }

                    for (var i = 0; i < chain.Levels.Count; i++)
                    {
                        var level = chain.Levels[i];
                        ValidateUrl($"streams.levels[{i}].request.url", level.Request.Url, vars, Error);
                        ValidatePath($"streams.levels[{i}].root", level.Root, Error);
                        ValidateFields($"streams.levels[{i}].fields", level.Fields, StreamFields, Error, Warn);
                        if (i == chain.Levels.Count - 1 && (level.Fields is null || !level.Fields.ContainsKey("url")))
                        {
                            Error($"streams.levels[{i}].fields.url", "The last level must map 'url'.");
                        }
                    }
                }

                break;
            case ProviderKind.M3u:
                if (def.M3u is null || string.IsNullOrWhiteSpace(def.M3u.Url))
                {
                    Error("m3u.url", "The playlist URL is required.");
                }
                else
                {
                    ValidateUrl("m3u.url", def.M3u.Url, vars, Error);
                    if (!string.IsNullOrWhiteSpace(def.M3u.XmltvUrl))
                    {
                        ValidateUrl("m3u.xmltvUrl", def.M3u.XmltvUrl, vars, Error);
                    }

                    ValidateRegex("m3u.includeGroups", def.M3u.IncludeGroups, Error);
                    ValidateRegex("m3u.excludeGroups", def.M3u.ExcludeGroups, Error);
                    ValidateRegex("m3u.eventTitlePattern", def.M3u.EventTitlePattern, Error);
                }

                break;
            case ProviderKind.Xtream:
                if (def.Xtream is null || string.IsNullOrWhiteSpace(def.Xtream.Server))
                {
                    Error("xtream.server", "The server URL is required.");
                }
                else
                {
                    ValidateUrl("xtream.server", def.Xtream.Server, vars, Error);
                    if (string.IsNullOrWhiteSpace(def.Xtream.Username) || string.IsNullOrWhiteSpace(def.Xtream.Password))
                    {
                        Error("xtream", "username and password are required.");
                    }

                    ValidateRegex("xtream.includeCategories", def.Xtream.IncludeCategories, Error);
                    ValidateRegex("xtream.excludeCategories", def.Xtream.ExcludeCategories, Error);
                }

                break;
            case ProviderKind.Direct:
                if (def.Direct is null || string.IsNullOrWhiteSpace(def.Direct.Url))
                {
                    Error("direct.url", "The stream URL is required.");
                }
                else
                {
                    ValidateUrl("direct.url", def.Direct.Url, vars, Error);
                }

                break;
        }

        if (def.Refresh is { } refresh)
        {
            if (refresh.LiveSeconds is < 15 or > 3600)
            {
                Error("refresh.liveSeconds", "Between 15 and 3600 seconds.");
            }

            if (refresh.TodaySeconds is < 30 or > 21600)
            {
                Error("refresh.todaySeconds", "Between 30 and 21600 seconds.");
            }

            if (refresh.AllSeconds is < 60 or > 86400)
            {
                Error("refresh.allSeconds", "Between 60 and 86400 seconds.");
            }
        }

        if (def.Defaults?.Timezone is { Length: > 0 } tz && DateParser.FindZone(tz) is null)
        {
            Error("defaults.timezone", $"Unknown time zone '{tz}' (use an IANA id such as Europe/Rome).");
        }

        for (var i = 0; i < (def.CategoryRules?.Count ?? 0); i++)
        {
            ValidateRegex($"categoryRules[{i}].match", def.CategoryRules![i].Match, Error);
        }

        return r;
    }

    private static void ValidateEndpoint(string path, EndpointSpec ep, string role, TemplateContext vars, Action<string, string> error, Action<string, string> warn)
    {
        ValidateUrl(path + ".request.url", ep.Request.Url, vars, error);
        if (!string.IsNullOrEmpty(ep.Request.Method) && ep.Request.Method is not ("GET" or "POST"))
        {
            error(path + ".request.method", "Only GET and POST are allowed.");
        }

        ValidatePath(path + ".root", ep.Root, error);
        if (ep.RefreshSeconds is < 15 or > 86400)
        {
            error(path + ".refreshSeconds", "Between 15 and 86400 seconds.");
        }

        if (ep.Pagination is { } pg)
        {
            switch (pg.Type)
            {
                case PaginationType.Cursor when string.IsNullOrWhiteSpace(pg.CursorPath):
                    error(path + ".pagination.cursorPath", "Required for cursor pagination.");
                    break;
                case PaginationType.NextUrl when string.IsNullOrWhiteSpace(pg.NextUrlPath):
                    error(path + ".pagination.nextUrlPath", "Required for nextUrl pagination.");
                    break;
                case PaginationType.Page or PaginationType.Offset when string.IsNullOrWhiteSpace(pg.Param) && !ep.Request.Url.Contains("{page}", StringComparison.Ordinal) && !ep.Request.Url.Contains("{offset}", StringComparison.Ordinal):
                    error(path + ".pagination.param", "Set 'param' or use {page}/{offset} in the URL.");
                    break;
            }

            ValidatePath(path + ".pagination.cursorPath", pg.CursorPath, error);
            ValidatePath(path + ".pagination.nextUrlPath", pg.NextUrlPath, error);
            ValidatePath(path + ".pagination.totalPagesPath", pg.TotalPagesPath, error);
            if (pg.MaxPages is < 1 or > 100)
            {
                error(path + ".pagination.maxPages", "Between 1 and 100.");
            }
        }

        var fields = ep.Fields;
        if (fields is null || (fields.Count == 0 && fields.Participants is null))
        {
            if (role != "detail")
            {
                error(path + ".fields", "Map at least the fields 'id' and 'title' (or 'name').");
            }
        }
        else
        {
            ValidateFields(path + ".fields", fields, EventFields, error, warn);
            if (role == "events")
            {
                if (!fields.ContainsKey("title") && !fields.ContainsKey("name") && fields.Participants is null && !(fields.ContainsKey("homeTeam") && fields.ContainsKey("awayTeam")))
                {
                    error(path + ".fields.title", "Map 'title' (or 'name', participants, homeTeam/awayTeam).");
                }

                if (!fields.ContainsKey("start") && !fields.ContainsKey("isLive") && !fields.ContainsKey("status"))
                {
                    error(path + ".fields.start", "Map 'start' (or at least 'isLive'/'status' for events on air).");
                }

                if (!fields.ContainsKey("id"))
                {
                    warn(path + ".fields.id", "Without 'id' events are identified by title + start time.");
                }
            }

            if (role == "channels" && !fields.ContainsKey("name") && !fields.ContainsKey("title"))
            {
                error(path + ".fields.name", "Map 'name'.");
            }

            if (fields.Participants is { } ps)
            {
                ValidatePath(path + ".fields.participants.path", ps.Path, error);
            }
        }

        if (ep.Streams is { } streams)
        {
            if (!string.Equals(streams.Path.Trim(), "$", StringComparison.Ordinal))
            {
                ValidatePath(path + ".streams.path", streams.Path, error);
            }

            ValidateFields(path + ".streams.fields", streams.Fields, StreamFields, error, warn);
            if (!streams.Fields.ContainsKey("url"))
            {
                error(path + ".streams.fields.url", "Map 'url'.");
            }
        }

        for (var i = 0; i < (ep.SkipIf?.Count ?? 0); i++)
        {
            ValidateRegex($"{path}.skipIf[{i}].matches", ep.SkipIf![i].Matches, error);
        }
    }

    private static void ValidateFields(string path, IReadOnlyDictionary<string, FieldSpec>? fields, HashSet<string> allowed, Action<string, string> error, Action<string, string> warn)
    {
        if (fields is null)
        {
            return;
        }

        foreach (var kv in fields)
        {
            var p = path + "." + kv.Key;
            if (!allowed.Contains(kv.Key))
            {
                error(p, $"Unknown field '{kv.Key}'.");
                continue;
            }

            var spec = kv.Value;
            if (spec.Path is null && spec.Template is null && spec.Value is null)
            {
                error(p, "Set 'path', 'template' or 'value'.");
            }

            ValidatePath(p + ".path", spec.Path, error);
            if (spec.Timezone is { Length: > 0 } tz && DateParser.FindZone(tz) is null)
            {
                error(p + ".timezone", $"Unknown time zone '{tz}'.");
            }

            if (spec.Format is { Length: > 0 } fmt && fmt is not ("auto" or "unix" or "unixMs" or "iso8601" or "rfc1123"))
            {
                try
                {
                    _ = new DateTime(2026, 10, 25, 20, 45, 0, DateTimeKind.Unspecified).ToString(fmt, CultureInfo.InvariantCulture);
                }
                catch (FormatException)
                {
                    error(p + ".format", $"Invalid date pattern '{fmt}'.");
                }
            }

            for (var i = 0; i < (spec.Transforms?.Count ?? 0); i++)
            {
                ValidateTransform($"{p}.transforms[{i}]", spec.Transforms![i], error);
            }
        }

        _ = warn;
    }

    private static void ValidateTransform(string path, TransformSpec t, Action<string, string> error)
    {
        if (!TransformEngine.Operations.Contains(t.Op))
        {
            error(path + ".op", $"Unknown transform '{t.Op}'. Allowed: {string.Join(", ", TransformEngine.Operations.OrderBy(o => o, StringComparer.Ordinal))}.");
            return;
        }

        switch (t.Op)
        {
            case "map" when t.Table is null || t.Table.Count == 0:
                error(path + ".table", "'map' needs a non empty 'table'.");
                break;
            case "replace" or "extract" when string.IsNullOrEmpty(t.Pattern):
                error(path + ".pattern", $"'{t.Op}' needs a 'pattern'.");
                break;
            case "concat" when t.Values is null || t.Values.Count == 0:
                error(path + ".values", "'concat' needs 'values'.");
                break;
            case "template" or "prefix" or "suffix" or "default" when t.Value is null:
                error(path + ".value", $"'{t.Op}' needs a 'value'.");
                break;
        }

        ValidateRegex(path + ".pattern", t.Pattern, error);
    }

    private static void ValidateUrl(string path, string? template, TemplateContext vars, Action<string, string> error)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            error(path, "A URL is required.");
            return;
        }

        var rendered = vars.Render(template);
        if (!IsHttpUrl(rendered))
        {
            error(path, $"Renders to '{rendered}', which is not an absolute http(s) URL.");
        }
    }

    private static void ValidatePath(string path, string? jsonPath, Action<string, string> error)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            return;
        }

        if (!JsonPath.TryParse(jsonPath, out _, out var message))
        {
            error(path, message ?? "Invalid JSONPath.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Admin-authored patterns are a feature; every Regex has a match timeout (ReDoS safe).")]
    private static void ValidateRegex(string path, string? pattern, Action<string, string> error)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }

        if (pattern.Length > 1000)
        {
            error(path, "Regex too long (max 1000).");
            return;
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            error(path, "Invalid regex: " + ex.Message);
        }
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
