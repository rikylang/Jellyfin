using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Maps JSON items to the internal model using the field mapping of a definition.
/// Language/country precedence: mapped field → definition defaults → heuristics (title, category, labels).
/// </summary>
public static class ItemMapper
{
    private static readonly Regex VersusRegex = new(
        @"^\s*(?<a>.+?)\s+(?:vs\.?|v\.?|-|–|—|x|@)\s+(?<b>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Maps an event.</summary>
    /// <param name="item">The JSON item.</param>
    /// <param name="root">The document root.</param>
    /// <param name="endpoint">The endpoint (fields, inline streams, skip rules).</param>
    /// <param name="ctx">The run context.</param>
    /// <param name="vars">Iteration variables (category, date).</param>
    /// <returns>The event, or null when skipped.</returns>
    public static LiveEvent? MapEvent(JsonNode? item, JsonNode? root, EndpointSpec endpoint, ProviderRunContext ctx, TemplateContext vars)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(vars);
        var f = endpoint.Fields ?? new FieldMap();
        var t = vars.Fork();
        t.Root = root;
        t.Current = item;

        if (ShouldSkip(endpoint.SkipIf, f, item, t))
        {
            return null;
        }

        var def = ctx.Definition;
        string? Text(string key) => f.TryGetValue(key, out var spec) ? FieldExtractor.Text(spec, item, t) : null;

        var participants = MapParticipants(f, item, t, ctx.BaseUrl);
        var title = Text("title") ?? Text("name");
        if (title is null && participants.Count >= 2)
        {
            title = participants[0].Name + " vs " + participants[1].Name;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            ctx.Diagnostics.Warn("An item without title was skipped (map 'title').");
            return null;
        }

        title = CleanTitle(title);
        if (participants.Count == 0)
        {
            participants = ParticipantsFromTitle(title);
        }

        var now = ctx.Clock.GetUtcNow();
        var start = f.TryGetValue("start", out var startSpec) ? FieldExtractor.Date(startSpec, item, t, ctx.TimeZone) : null;
        var end = f.TryGetValue("end", out var endSpec) ? FieldExtractor.Date(endSpec, item, t, ctx.TimeZone) : null;
        var duration = f.TryGetValue("durationMinutes", out var durSpec) ? FieldExtractor.Number(durSpec, item, t) : null;
        var isLive = f.TryGetValue("isLive", out var liveSpec) ? FieldExtractor.Bool(liveSpec, item, t) : null;
        var status = ParseStatus(Text("status"));
        if (status == EventStatus.Live)
        {
            isLive = true;
        }

        if (start is null)
        {
            if (isLive == true)
            {
                start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
            }
            else
            {
                ctx.Diagnostics.Warn($"'{title}' was skipped: missing or unparsable start time (check 'start' and its format).");
                return null;
            }
        }

        var externalId = Text("id") ?? Keys.Hash(title + "|" + start.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        var ev = new LiveEvent
        {
            ProviderId = ctx.ProviderId,
            ExternalId = externalId,
            Key = Keys.Event(ctx.ProviderId, externalId),
            Title = title,
            Category = Text("category") ?? def.Defaults?.Category,
            CategoryId = Text("categoryId") ?? vars["category.id"],
            Competition = Text("competition"),
            Start = start.Value,
            End = end is not null && end > start ? end : null,
            DurationMinutes = duration is > 0 and < 2000 ? (int)duration.Value : null,
            IsLiveFlag = isLive,
            StatusFlag = status,
            Popularity = f.TryGetValue("popularity", out var popSpec) ? FieldExtractor.Number(popSpec, item, t) : null,
            PosterUrl = Url(Text("poster"), ctx),
            ThumbUrl = Url(Text("thumb") ?? Text("image"), ctx),
            LogoUrl = Url(Text("logo"), ctx),
            Participants = participants,
            Description = Text("description"),
            PageUrl = Url(Text("pageUrl"), ctx),
            ContentRating = Text("contentRating") ?? def.Defaults?.ContentRating,
            Priority = ctx.Priority,
        };

        if (ev.Category is null && vars["category.name"] is { } catName)
        {
            ev.Category = catName;
        }

        if (f.TryGetValue("tags", out var tagSpec))
        {
            ev.Tags.AddRange(FieldExtractor.Evaluate(tagSpec, item, t).All().Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (def.Defaults?.Tags is { } defaultTags)
        {
            ev.Tags.AddRange(defaultTags.Where(x => !ev.Tags.Contains(x, StringComparer.OrdinalIgnoreCase)));
        }

        ApplyHomeAwayLogos(ev, f, item, t, ctx);
        ApplyCategoryRules(ev, def);

        ev.Language = LocaleNormalizer.NormalizeLanguage(Text("language")) ?? ev.Language ?? LocaleNormalizer.NormalizeLanguage(def.Defaults?.Language) ?? LocaleNormalizer.GuessLanguage(ev.Title);
        ev.Country = LocaleNormalizer.NormalizeCountry(Text("country")) ?? ev.Country ?? LocaleNormalizer.NormalizeCountry(def.Defaults?.Country) ?? LocaleNormalizer.GuessCountry(ev.Title);
        ev.SourceProviders.Add(ctx.ProviderId);
        ev.MergedRefs.Add(ctx.ProviderId + "\u001f" + externalId);

        ev.Context["id"] = ev.ExternalId;
        ev.Context["title"] = ev.Title;
        ev.Context["start"] = ev.Start.ToString("o", CultureInfo.InvariantCulture);
        ev.Context["startUnix"] = ev.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        foreach (var kv in f)
        {
            if (!ev.Context.ContainsKey(kv.Key))
            {
                var v = FieldExtractor.Text(kv.Value, item, t);
                if (v is not null)
                {
                    ev.Context[kv.Key] = v;
                }
            }
        }

        if (endpoint.Streams is not null)
        {
            var et = t.Fork();
            et.SetAll("event.", ev.Context);
            foreach (var node in SelectRelative(endpoint.Streams.Path, item))
            {
                var stream = MapStream(node, endpoint.Streams.Fields, ctx, et, ev.ExternalId, ev.Language, ev.Country, null);
                if (stream is not null && !ev.Streams.Exists(s => s.Id == stream.Id))
                {
                    stream.Priority = ctx.Priority;
                    ev.Streams.Add(stream);
                }
            }
        }

        ev.Language ??= ev.Streams.Select(s => s.Language).FirstOrDefault(l => l is not null);
        ev.Country ??= LocaleNormalizer.CountryForLanguage(ev.Language);
        return ev;
    }

    /// <summary>Maps a 24/7 channel.</summary>
    /// <param name="item">The item.</param>
    /// <param name="root">The root.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="vars">Variables.</param>
    /// <returns>The channel or null.</returns>
    public static LiveChannel? MapChannel(JsonNode? item, JsonNode? root, EndpointSpec endpoint, ProviderRunContext ctx, TemplateContext vars)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(vars);
        var f = endpoint.Fields ?? new FieldMap();
        var t = vars.Fork();
        t.Root = root;
        t.Current = item;
        if (ShouldSkip(endpoint.SkipIf, f, item, t))
        {
            return null;
        }

        string? Text(string key) => f.TryGetValue(key, out var spec) ? FieldExtractor.Text(spec, item, t) : null;
        var name = Text("name") ?? Text("title");
        if (string.IsNullOrWhiteSpace(name))
        {
            ctx.Diagnostics.Warn("A channel without name was skipped (map 'name').");
            return null;
        }

        var externalId = Text("id") ?? Text("epgId") ?? Keys.Hash(name);
        var ch = new LiveChannel
        {
            ProviderId = ctx.ProviderId,
            ExternalId = externalId,
            Key = Keys.Channel(ctx.ProviderId, externalId),
            Name = name.Trim(),
            Number = Text("number"),
            Category = Text("category") ?? ctx.Definition.Defaults?.Category,
            LogoUrl = Url(Text("logo") ?? Text("image"), ctx),
            EpgId = Text("epgId"),
            Description = Text("description"),
            ContentRating = Text("contentRating") ?? ctx.Definition.Defaults?.ContentRating,
            Priority = ctx.Priority,
        };
        ch.Language = LocaleNormalizer.NormalizeLanguage(Text("language")) ?? LocaleNormalizer.NormalizeLanguage(ctx.Definition.Defaults?.Language) ?? LocaleNormalizer.GuessLanguage(ch.Name);
        ch.Country = LocaleNormalizer.NormalizeCountry(Text("country")) ?? LocaleNormalizer.NormalizeCountry(ctx.Definition.Defaults?.Country) ?? LocaleNormalizer.GuessCountry(ch.Name) ?? LocaleNormalizer.CountryForLanguage(ch.Language);
        if (ctx.Definition.Defaults?.Tags is { } tags)
        {
            ch.Tags.AddRange(tags);
        }

        if (endpoint.Streams is not null)
        {
            var ct = t.Fork();
            ct.SetAll("channel.", new Dictionary<string, string> { ["id"] = externalId, ["name"] = ch.Name });
            foreach (var node in SelectRelative(endpoint.Streams.Path, item))
            {
                var stream = MapStream(node, endpoint.Streams.Fields, ctx, ct, externalId, ch.Language, ch.Country, null);
                if (stream is not null && !ch.Streams.Exists(s => s.Id == stream.Id))
                {
                    stream.Priority = ctx.Priority;
                    ch.Streams.Add(stream);
                }
            }
        }

        return ch;
    }

    /// <summary>Maps a category.</summary>
    /// <param name="item">The item.</param>
    /// <param name="root">The root.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="ctx">The context.</param>
    /// <returns>The category or null.</returns>
    public static LiveCategory? MapCategory(JsonNode? item, JsonNode? root, EndpointSpec endpoint, ProviderRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(ctx);
        var f = endpoint.Fields ?? new FieldMap();
        var t = ctx.Template.Fork();
        t.Root = root;
        t.Current = item;
        string? Text(string key) => f.TryGetValue(key, out var spec) ? FieldExtractor.Text(spec, item, t) : null;
        var id = Text("id") ?? Text("name");
        if (id is null)
        {
            return null;
        }

        return new LiveCategory { ProviderId = ctx.ProviderId, Id = id, Name = Text("name") ?? id, ImageUrl = Url(Text("image") ?? Text("logo"), ctx) };
    }

    /// <summary>Maps a stream node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="fields">Stream fields.</param>
    /// <param name="ctx">Run context.</param>
    /// <param name="t">Template context (event/source variables).</param>
    /// <param name="owner">Owner external id (key scope).</param>
    /// <param name="fallbackLanguage">Owner language.</param>
    /// <param name="fallbackCountry">Owner country.</param>
    /// <param name="inherited">Attributes inherited from previous chain levels.</param>
    /// <returns>The stream or null.</returns>
    public static LiveStream? MapStream(
        JsonNode? node,
        IReadOnlyDictionary<string, FieldSpec>? fields,
        ProviderRunContext ctx,
        TemplateContext t,
        string owner,
        string? fallbackLanguage,
        string? fallbackCountry,
        LiveStream? inherited)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(t);
        fields ??= new Dictionary<string, FieldSpec>();
        var local = t.Fork();
        local.Current = node;
        string? Text(string key) => fields.TryGetValue(key, out var spec) ? FieldExtractor.Text(spec, node, local) : null;

        var url = Url(Text("url"), ctx);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ctx.Diagnostics.Warn("A stream with a non http(s) URL was ignored.");
            return null;
        }

        var quality = Text("quality") ?? inherited?.QualityLabel;
        var heightValue = fields.TryGetValue("height", out var hSpec) ? FieldExtractor.Number(hSpec, node, local) : null;
        var height = heightValue is > 100 and < 5000 ? (int)heightValue.Value : StreamClassifier.HeightFromLabel(quality) ?? inherited?.Height;
        if (height is null && fields.TryGetValue("hd", out var hdSpec) && FieldExtractor.Bool(hdSpec, node, local) == true)
        {
            height = 720;
        }

        var label = Text("label") ?? inherited?.Label;
        var stream = new LiveStream
        {
            ProviderId = ctx.ProviderId,
            Url = url,
            Kind = StreamClassifier.Classify(Text("type"), url),
            QualityLabel = quality ?? StreamClassifier.LabelFromHeight(height),
            Height = height,
            Bitrate = StreamClassifier.NormalizeBitrate(fields.TryGetValue("bitrate", out var bSpec) ? FieldExtractor.Number(bSpec, node, local) : null) ?? inherited?.Bitrate,
            Label = label,
            Priority = ctx.Priority,
        };

        stream.Language = LocaleNormalizer.NormalizeLanguage(Text("language"))
            ?? inherited?.Language
            ?? LocaleNormalizer.GuessLanguage(label)
            ?? fallbackLanguage
            ?? LocaleNormalizer.NormalizeLanguage(ctx.Definition.Defaults?.Language);
        stream.Country = LocaleNormalizer.NormalizeCountry(Text("country"))
            ?? inherited?.Country
            ?? fallbackCountry
            ?? LocaleNormalizer.NormalizeCountry(ctx.Definition.Defaults?.Country)
            ?? LocaleNormalizer.CountryForLanguage(stream.Language);

        if (inherited is not null)
        {
            foreach (var h in inherited.Headers)
            {
                stream.Headers[h.Key] = h.Value;
            }
        }

        foreach (var h in FieldExtractor.Headers(fields.GetValueOrDefault("headers"), node, local))
        {
            stream.Headers[h.Key] = h.Value;
        }

        if (Text("referer") is { } referer)
        {
            stream.Headers["Referer"] = referer;
        }

        if (Text("userAgent") is { } ua)
        {
            stream.Headers["User-Agent"] = ua;
        }

        if (fields.TryGetValue("expiresAt", out var expSpec))
        {
            stream.ExpiresAt = FieldExtractor.Date(expSpec, node, local, ctx.TimeZone);
        }

        var mappedId = Text("id");
        stream.Id = Keys.Stream(ctx.ProviderId, owner, mappedId ?? url);
        return stream;
    }

    /// <summary>Selects nodes with a path relative to an item ("$" = the item itself).</summary>
    /// <param name="path">The path.</param>
    /// <param name="item">The item.</param>
    /// <returns>The nodes.</returns>
    public static IReadOnlyList<JsonNode?> SelectRelative(string? path, JsonNode? item)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "$" || path.Trim() == "@")
        {
            return new[] { item };
        }

        var compiled = JsonPath.Parse(path);
        var result = compiled.Select(item, item);

        // A path selecting an array (without [*]) means "each element".
        if (result.Count == 1 && result[0] is JsonArray arr)
        {
            return arr.ToList();
        }

        return result;
    }

    /// <summary>Parses a status text.</summary>
    /// <param name="status">The text.</param>
    /// <returns>The status.</returns>
    public static EventStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var s = status.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal).Replace(" ", "_", StringComparison.Ordinal);
        return s switch
        {
            "live" or "inplay" or "in_play" or "in_progress" or "inprogress" or "playing" or "started" or "running" or "on_air" or "onair" or "1h" or "2h" or "ht" or "halftime" or "true" => EventStatus.Live,
            "ended" or "finished" or "ft" or "fulltime" or "full_time" or "completed" or "closed" or "over" or "final" or "aet" or "pen" or "cancelled" or "canceled" or "postponed" => EventStatus.Ended,
            "upcoming" or "scheduled" or "not_started" or "notstarted" or "ns" or "pre" or "pre_match" or "prematch" or "tbd" or "false" => EventStatus.Upcoming,
            _ => null,
        };
    }

    /// <summary>Derives participants from "A vs B", "A - B", "A v B".</summary>
    /// <param name="title">The title.</param>
    /// <returns>The participants.</returns>
    public static List<Participant> ParticipantsFromTitle(string title)
    {
        try
        {
            var m = VersusRegex.Match(title);
            if (m.Success && m.Groups["a"].Value.Length <= 60 && m.Groups["b"].Value.Length <= 60)
            {
                return new List<Participant> { new() { Name = m.Groups["a"].Value.Trim() }, new() { Name = m.Groups["b"].Value.Trim() } };
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return new List<Participant>();
    }

    private static string CleanTitle(string title)
    {
        var t = System.Net.WebUtility.HtmlDecode(title).Trim();
        while (t.Contains("  ", StringComparison.Ordinal))
        {
            t = t.Replace("  ", " ", StringComparison.Ordinal);
        }

        return t;
    }

    private static List<Participant> MapParticipants(FieldMap f, JsonNode? item, TemplateContext t, string baseUrl)
    {
        var list = new List<Participant>();
        if (f.Participants is { } ps && !string.IsNullOrWhiteSpace(ps.Path))
        {
            foreach (var node in SelectRelative(ps.Path, item))
            {
                var name = ps.Name is not null
                    ? FieldExtractor.Text(ps.Name, node, t)
                    : (node is JsonValue ? JsonPath.ToText(node) : null);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var logo = ps.Logo is null ? null : FieldExtractor.Text(ps.Logo, node, t);
                    list.Add(new Participant { Name = name.Trim(), LogoUrl = TransformEngine.AbsoluteUrl(baseUrl, logo) });
                }
            }
        }

        if (list.Count == 0)
        {
            var home = f.TryGetValue("homeTeam", out var h) ? FieldExtractor.Text(h, item, t) : null;
            var away = f.TryGetValue("awayTeam", out var a) ? FieldExtractor.Text(a, item, t) : null;
            if (home is not null && away is not null)
            {
                list.Add(new Participant { Name = home });
                list.Add(new Participant { Name = away });
            }
        }

        return list;
    }

    private static void ApplyHomeAwayLogos(LiveEvent ev, FieldMap f, JsonNode? item, TemplateContext t, ProviderRunContext ctx)
    {
        if (ev.Participants.Count >= 1 && f.TryGetValue("homeLogo", out var hl))
        {
            ev.Participants[0].LogoUrl ??= Url(FieldExtractor.Text(hl, item, t), ctx);
        }

        if (ev.Participants.Count >= 2 && f.TryGetValue("awayLogo", out var al))
        {
            ev.Participants[1].LogoUrl ??= Url(FieldExtractor.Text(al, item, t), ctx);
        }
    }

    private static void ApplyCategoryRules(LiveEvent ev, ProviderDefinition def)
    {
        if (def.CategoryRules is null)
        {
            return;
        }

        var haystack = string.Join(" | ", new[] { ev.Category, ev.Competition, ev.Title }.Where(s => !string.IsNullOrEmpty(s)));
        foreach (var rule in def.CategoryRules)
        {
            try
            {
                if (!TransformEngine.GetRegex(rule.Match).IsMatch(haystack))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.Category))
            {
                ev.Category = rule.Category;
            }

            ev.Country ??= LocaleNormalizer.NormalizeCountry(rule.Country);
            ev.Language ??= LocaleNormalizer.NormalizeLanguage(rule.Language);
            ev.DurationMinutes ??= rule.DurationMinutes;
        }
    }

    private static bool ShouldSkip(List<ConditionSpec>? conditions, FieldMap f, JsonNode? item, TemplateContext t)
    {
        if (conditions is null || conditions.Count == 0)
        {
            return false;
        }

        foreach (var c in conditions)
        {
            string? value;
            if (c.Field.StartsWith('$') || c.Field.StartsWith('@'))
            {
                value = FieldExtractor.Text(FieldSpec.OfPath(c.Field), item, t);
            }
            else
            {
                value = f.TryGetValue(c.Field, out var spec) ? FieldExtractor.Text(spec, item, t) : null;
            }

            if (c.Missing == true && value is null)
            {
                return true;
            }

            if (c.EqualsValue is not null && string.Equals(value, c.EqualsValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (c.NotEquals is not null && value is not null && !string.Equals(value, c.NotEquals, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (c.Matches is not null && value is not null)
            {
                try
                {
                    if (TransformEngine.GetRegex(c.Matches).IsMatch(value))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                }
            }
        }

        return false;
    }

    private static string? Url(string? value, ProviderRunContext ctx) => TransformEngine.AbsoluteUrl(ctx.BaseUrl, value);
}
