using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Locale;

namespace Jellyfin.Plugin.LiveHub.Wizard;

/// <summary>
/// Proposes a field mapping for an unknown JSON API, using field names (title/name, start/date/kickoff, url/stream/src,
/// lang, quality, logo/poster...) and value types (timestamps, image URLs, .m3u8/.mpd URLs).
/// </summary>
public static class MappingSuggester
{
    private static readonly string[] DatePatterns =
    {
        "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm", "MM/dd/yyyy hh:mm tt", "dd-MM-yyyy HH:mm", "yyyy/MM/dd HH:mm", "yyyyMMddHHmmss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
    };

    private static readonly System.Buffers.SearchValues<char> OffsetSigns = System.Buffers.SearchValues.Create("+-");
    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
    private static readonly Regex ImageExt = new(@"\.(png|jpe?g|webp|gif|svg|avif)(\?|$)|/(img|images?|logos?|posters?|thumbs?|thumbnails?|badges?|crests?)/", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    private static readonly Regex StreamExt = new(@"\.(m3u8|mpd|mp4|ts)(\?|$)|m3u8|/hls/|/dash/|/live/", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Identifier = new("^[A-Za-z_$][A-Za-z0-9_$-]*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private static readonly Dictionary<string, string[]> EventKeywords = new(StringComparer.Ordinal)
    {
        ["id"] = new[] { "id", "uid", "uuid", "guid", "eventid", "matchid", "gameid", "fixtureid", "key", "slug", "code", "streamid", "channelid", "num" },
        ["title"] = new[] { "title", "name", "eventname", "event", "matchname", "match", "label", "headline", "display", "displayname", "fullname", "channelname" },
        ["start"] = new[] { "start", "starttime", "startdate", "startsat", "startat", "starts", "begin", "kickoff", "kickofftime", "date", "datetime", "time", "scheduled", "scheduledat", "timestamp", "utcstart", "commencetime", "airtime", "eventdate", "eventtime", "starttimestamp", "startutc" },
        ["end"] = new[] { "end", "endtime", "enddate", "endsat", "endat", "stop", "finish", "until", "endutc" },
        ["isLive"] = new[] { "live", "islive", "inplay", "isinplay", "onair", "livenow", "streaming" },
        ["status"] = new[] { "status", "state", "matchstatus", "eventstatus", "phase" },
        ["category"] = new[] { "sport", "category", "genre", "discipline", "sportname", "categoryname", "group", "grouptitle" },
        ["competition"] = new[] { "league", "competition", "tournament", "championship", "series", "cup", "leaguename", "competitionname" },
        ["popularity"] = new[] { "viewers", "popularity", "views", "watchers", "rank", "hits", "listeners", "audience" },
        ["poster"] = new[] { "poster", "posterurl", "portrait", "cover" },
        ["thumb"] = new[] { "thumbnail", "thumb", "image", "img", "picture", "banner", "preview", "photo", "imageurl", "background", "artwork", "streamicon" },
        ["logo"] = new[] { "logo", "icon", "badge", "emblem", "crest", "channellogo", "tvglogo" },
        ["country"] = new[] { "country", "region", "nation", "countrycode", "area", "geo" },
        ["language"] = new[] { "language", "lang", "audio", "locale", "audiolang", "commentary" },
        ["description"] = new[] { "description", "desc", "summary", "overview", "details", "info", "synopsis" },
        ["pageUrl"] = new[] { "page", "permalink", "pageurl", "eventurl", "link", "href" },
        ["epgId"] = new[] { "epgid", "epgchannelid", "tvgid", "xmltvid" },
        ["number"] = new[] { "number", "channelnumber", "chno", "lcn" },
    };

    private static readonly Dictionary<string, string[]> StreamKeywords = new(StringComparer.Ordinal)
    {
        ["url"] = new[] { "url", "src", "file", "link", "stream", "streamurl", "hls", "m3u8", "source", "embed", "iframe", "href", "playback", "manifest", "uri", "playurl", "embedurl" },
        ["quality"] = new[] { "quality", "resolution", "res", "label", "q", "definition", "qualitylabel" },
        ["height"] = new[] { "height", "h" },
        ["bitrate"] = new[] { "bitrate", "bandwidth", "kbps", "bps" },
        ["language"] = new[] { "language", "lang", "audio", "locale", "audiolang" },
        ["country"] = new[] { "country", "region" },
        ["type"] = new[] { "type", "format", "kind", "mime", "mimetype", "protocol" },
        ["label"] = new[] { "name", "provider", "source", "server", "title", "channel", "host" },
        ["hd"] = new[] { "hd", "ishd" },
        ["headers"] = new[] { "headers" },
        ["referer"] = new[] { "referer", "referrer" },
        ["id"] = new[] { "id", "sourceid", "streamid" },
    };

    private static readonly HashSet<string> GenericLeaf = new(StringComparer.Ordinal) { "name", "label", "title", "display", "value", "text", "url", "src", "short", "full", "en", "default", "slug" };

    private static readonly string[] StreamArrayNames = { "streams", "sources", "links", "feeds", "broadcasts", "servers", "mirrors", "players", "embeds", "urls", "channels", "videos", "media" };
    private static readonly string[] ParticipantArrayNames = { "teams", "competitors", "participants", "players", "sides", "opponents", "contestants" };

    /// <summary>Suggests a mapping for a JSON body.</summary>
    /// <param name="root">The body.</param>
    /// <param name="headers">Response headers (Link detection).</param>
    /// <param name="documentation">Optional documentation text.</param>
    /// <returns>The suggestion.</returns>
    public static MappingSuggestion Suggest(JsonNode? root, IReadOnlyDictionary<string, string>? headers, string? documentation)
    {
        var result = new MappingSuggestion();
        if (root is null)
        {
            result.Warnings.Add("Empty response.");
            return result;
        }

        var candidates = new List<(string Path, JsonArray Array, double Score, int Depth, bool Nested)>();
        FindArrays(root, "$", 0, false, candidates);
        if (root is JsonObject single && candidates.Count == 0)
        {
            // A single object (detail endpoint).
            candidates.Add(("$", new JsonArray(single.DeepClone()), 1, 0, false));
        }

        if (candidates.Count == 0)
        {
            result.Warnings.Add("No array of objects found in the response.");
            return result;
        }

        var best = candidates.OrderByDescending(c => c.Score).First();
        result.Root = best.Path == "$" && root is JsonObject ? null : best.Path;
        var items = best.Array.OfType<JsonObject>().Take(25).ToList();
        result.ItemCount = best.Array.Count;
        var docHints = Hints(documentation);

        // Leaves and nested arrays of the items.
        var leaves = new Dictionary<string, List<JsonNode?>>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, List<JsonArray>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            Flatten(item, "$", 0, leaves, arrays);
        }

        // Streams (arrays of objects/strings with stream URLs, or a URL leaf on the item).
        DetectStreams(result, leaves, arrays);

        // Participants.
        foreach (var (path, list) in arrays)
        {
            var last = LastKey(path);
            if (ParticipantArrayNames.Contains(last) && list.Any(a => a.OfType<JsonObject>().Any()))
            {
                var elems = list.SelectMany(a => a.OfType<JsonObject>()).Take(20).ToList();
                var nameKey = BestKey(elems, new[] { "name", "title", "displayname", "fullname", "team", "shortname" }, v => IsTextual(v));
                if (nameKey is not null)
                {
                    var logoKey = BestKey(elems, new[] { "logo", "badge", "image", "crest", "icon", "img", "flag" }, v => IsImageUrl(JsonPath.ToText(v)));
                    result.Participants = new ParticipantsSpec
                    {
                        Path = path + "[*]",
                        Name = FieldSpec.OfPath("$" + Seg(nameKey)),
                        Logo = logoKey is null ? null : FieldSpec.OfPath("$" + Seg(logoKey)),
                    };
                    break;
                }
            }
        }

        // Score every leaf for every target field and assign greedily.
        var scores = new List<(string Field, string Path, double Score)>();
        foreach (var (path, values) in leaves)
        {
            if (result.StreamUrlLeaf == path)
            {
                continue;
            }

            foreach (var (field, words) in EventKeywords)
            {
                var s = NameScore(path, field, words, EventKeywords) + TypeScore(field, values);
                if (docHints.Contains(Normalize(LastKey(path))) && s > 0.3)
                {
                    s += 0.1;
                }

                if (s >= 0.6)
                {
                    scores.Add((field, path, s));
                }
            }
        }

        var usedPaths = new HashSet<string>(StringComparer.Ordinal);
        var fieldOrder = new[] { "start", "title", "id", "end", "isLive", "status", "competition", "category", "logo", "poster", "thumb", "popularity", "country", "language", "description", "epgId", "number", "pageUrl" };
        foreach (var field in fieldOrder)
        {
            var pick = scores.Where(s => s.Field == field && !usedPaths.Contains(s.Path)).OrderByDescending(s => s.Score).FirstOrDefault();
            if (pick.Path is null)
            {
                continue;
            }

            usedPaths.Add(pick.Path);
            var spec = FieldSpec.OfPath(pick.Path);
            var sample = JsonPath.ToText(leaves[pick.Path].FirstOrDefault(v => v is not null));
            string? format = null;
            if (field is "start" or "end")
            {
                format = DetectDateFormat(leaves[pick.Path]);
                spec = new FieldSpec { Path = pick.Path, Format = format };
            }
            else if (field == "isLive" && !leaves[pick.Path].All(v => JsonPath.ToText(v) is "true" or "false"))
            {
                spec = new FieldSpec { Path = pick.Path, Transforms = new List<TransformSpec> { new() { Op = "boolean" } } };
            }
            else if (field is "logo" or "poster" or "thumb" or "pageUrl" && leaves[pick.Path].Any(v => JsonPath.ToText(v)?.StartsWith('/') == true))
            {
                spec = new FieldSpec { Path = pick.Path, Transforms = new List<TransformSpec> { new() { Op = "absoluteUrl" } } };
            }

            result.Fields[field] = spec;
            result.Report.Add(new FieldSuggestion { Field = field, Path = pick.Path, Confidence = Math.Round(Math.Min(1, pick.Score / 1.5), 2), Sample = Trim(sample), Format = format });
        }

        // Home/away teams.
        if (result.Participants is null)
        {
            var home = leaves.Keys.FirstOrDefault(p => Regex.IsMatch(Normalize(p), "(home|hometeam|team1|local|teama)(name)?$", RegexOptions.None, TimeSpan.FromMilliseconds(50)) && leaves[p].Any(v => IsTextual(v)));
            var away = leaves.Keys.FirstOrDefault(p => Regex.IsMatch(Normalize(p), "(away|awayteam|team2|visitor|visitors|teamb|guest)(name)?$", RegexOptions.None, TimeSpan.FromMilliseconds(50)) && leaves[p].Any(v => IsTextual(v)));
            if (home is not null && away is not null && !usedPaths.Contains(home) && !usedPaths.Contains(away))
            {
                result.Fields["homeTeam"] = FieldSpec.OfPath(home);
                result.Fields["awayTeam"] = FieldSpec.OfPath(away);
                result.Report.Add(new FieldSuggestion { Field = "homeTeam", Path = home, Confidence = 0.8, Sample = Trim(JsonPath.ToText(leaves[home].FirstOrDefault())) });
                result.Report.Add(new FieldSuggestion { Field = "awayTeam", Path = away, Confidence = 0.8, Sample = Trim(JsonPath.ToText(leaves[away].FirstOrDefault())) });

                // "Home vs Away" beats a weak title guess.
                var titleReport = result.Report.FirstOrDefault(r => r.Field == "title");
                if (titleReport is null || titleReport.Confidence < 0.65)
                {
                    if (titleReport is not null)
                    {
                        result.Report.Remove(titleReport);
                    }

                    var template = "{" + home + "} vs {" + away + "}";
                    result.Fields["title"] = FieldSpec.FromShorthand(template);
                    result.Report.Add(new FieldSuggestion { Field = "title", Path = template, Confidence = 0.8 });
                }
            }
        }

        if (result.Participants is not null)
        {
            result.Report.Add(new FieldSuggestion { Field = "participants", Path = result.Participants.Path, Confidence = 0.8 });
        }

        // Role: channels when there is no time information but there are streams.
        result.Role = !result.Fields.ContainsKey("start") && !result.Fields.ContainsKey("isLive") && !result.Fields.ContainsKey("status") && result.Streams is not null
            ? "channels"
            : "events";
        if (result.Role == "channels" && result.Fields.TryGetValue("title", out var t))
        {
            result.Fields.Remove("title");
            result.Fields["name"] = t;
            result.Report.First(r => r.Field == "title").Field = "name";
        }

        if (result.Role == "events" && !result.Fields.ContainsKey("start"))
        {
            result.Warnings.Add("No start time recognised: map 'start' manually (or 'isLive' for an on-air list).");
        }

        if (!result.Fields.ContainsKey("title") && !result.Fields.ContainsKey("name") && result.Participants is null && !result.Fields.ContainsKey("homeTeam"))
        {
            result.Warnings.Add("No title recognised: map 'title' manually.");
        }

        if (result.Streams is null)
        {
            result.Warnings.Add("No stream URL recognised in the items: map a stream chain (separate endpoint) or the URL field manually.");
        }

        result.Pagination = DetectPagination(root, best.Path, headers);
        return result;
    }

    /// <summary>Detects the format of date values.</summary>
    /// <param name="values">Sample values.</param>
    /// <returns>Format string.</returns>
    public static string DetectDateFormat(IReadOnlyList<JsonNode?> values)
    {
        var texts = values.Select(JsonPath.ToText).Where(s => !string.IsNullOrWhiteSpace(s)).Take(15).ToList();
        if (texts.Count == 0)
        {
            return "auto";
        }

        if (texts.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            var n = double.Parse(texts[0]!, NumberStyles.Float, CultureInfo.InvariantCulture);
            return n > 1e11 ? "unixMs" : "unix";
        }

        // "2026-10-25T20:45:00+01:00" (ISO with T or with an offset) vs "2026-10-25 20:45:00" (custom pattern, wall clock).
        if (texts.All(s => IsoDate.IsMatch(s!) && (s!.Contains('T', StringComparison.Ordinal) || s.EndsWith('Z') || s.AsSpan().LastIndexOfAny(OffsetSigns) > 10)))
        {
            return "iso8601";
        }

        foreach (var pattern in DatePatterns)
        {
            if (texts.All(s => DateTime.TryParseExact(s, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _)))
            {
                return pattern;
            }
        }

        return texts.All(s => IsoDate.IsMatch(s!)) ? "iso8601" : "auto";
    }

    private static void FindArrays(JsonNode node, string path, int depth, bool nested, List<(string Path, JsonArray Array, double Score, int Depth, bool Nested)> output)
    {
        if (depth > 6)
        {
            return;
        }

        if (node is JsonArray arr)
        {
            var objects = arr.OfType<JsonObject>().Take(30).ToList();
            if (objects.Count > 0 && objects.Count * 2 >= Math.Min(arr.Count, 30))
            {
                output.Add((path + "[*]", arr, ScoreArray(path, objects, arr.Count, depth, nested), depth, nested));
            }

            foreach (var o in objects.Take(1))
            {
                FindArrays(o, path + "[0]", depth + 1, true, output);
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value is not null)
                {
                    FindArrays(kv.Value, path + Seg(kv.Key), depth + 1, nested, output);
                }
            }
        }
    }

    private static double ScoreArray(string path, List<JsonObject> objects, int count, int depth, bool nested)
    {
        var last = LastKey(path);
        var score = Math.Log(count + 1) * 1.5;
        if (Regex.IsMatch(last, "^(events?|items|data|results|matches|fixtures|games|schedule|list|entries|programs|programmes|broadcasts|lives?|live_streams|channels|records|rows|content)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
        {
            score += 3;
        }

        if (StreamArrayNames.Contains(last) && nested)
        {
            score -= 2;
        }

        var keys = objects.SelectMany(o => o.Select(kv => Normalize(kv.Key))).ToHashSet(StringComparer.Ordinal);
        if (keys.Overlaps(EventKeywords["title"]))
        {
            score += 3;
        }

        if (keys.Overlaps(EventKeywords["start"]))
        {
            score += 3;
        }

        if (objects.Any(o => o.ToJsonString().Contains("http", StringComparison.OrdinalIgnoreCase)))
        {
            score += 1;
        }

        score -= depth * 0.3;
        if (nested)
        {
            score -= 4;
        }

        return score;
    }

    private static void Flatten(JsonObject obj, string prefix, int depth, Dictionary<string, List<JsonNode?>> leaves, Dictionary<string, List<JsonArray>> arrays)
    {
        foreach (var kv in obj)
        {
            var path = prefix + Seg(kv.Key);
            switch (kv.Value)
            {
                case JsonObject o when depth < 3:
                    Flatten(o, path, depth + 1, leaves, arrays);
                    break;
                case JsonArray a:
                    if (!arrays.TryGetValue(path, out var al))
                    {
                        al = new List<JsonArray>();
                        arrays[path] = al;
                    }

                    al.Add(a);
                    break;
                case JsonObject:
                    break;
                default:
                    if (!leaves.TryGetValue(path, out var l))
                    {
                        l = new List<JsonNode?>();
                        leaves[path] = l;
                    }

                    l.Add(kv.Value);
                    break;
            }
        }
    }

    private static void DetectStreams(MappingSuggestion result, Dictionary<string, List<JsonNode?>> leaves, Dictionary<string, List<JsonArray>> arrays)
    {
        // 1) Arrays of objects with URL values.
        var best = arrays
            .Select(kv => (Path: kv.Key, Elems: kv.Value.SelectMany(a => a.OfType<JsonObject>()).Take(30).ToList(), Strings: kv.Value.SelectMany(a => a.OfType<JsonValue>()).Select(JsonPath.ToText).Take(30).ToList()))
            .Select(x => (x.Path, x.Elems, x.Strings, Score: StreamArrayScore(x.Path, x.Elems, x.Strings)))
            .Where(x => x.Score > 1)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best.Path is not null)
        {
            if (best.Elems.Count > 0)
            {
                var elemLeaves = new Dictionary<string, List<JsonNode?>>(StringComparer.Ordinal);
                var dummy = new Dictionary<string, List<JsonArray>>(StringComparer.Ordinal);
                foreach (var e in best.Elems)
                {
                    Flatten(e, "$", 1, elemLeaves, dummy);
                }

                var fields = MapStreamFields(elemLeaves, result.StreamReport);
                if (fields.ContainsKey("url"))
                {
                    result.Streams = new InlineStreamsSpec { Path = best.Path + "[*]", Fields = fields };
                    return;
                }
            }
            else if (best.Strings.Count > 0)
            {
                result.Streams = new InlineStreamsSpec { Path = best.Path + "[*]", Fields = new Dictionary<string, FieldSpec> { ["url"] = FieldSpec.OfPath("$") } };
                result.StreamReport.Add(new FieldSuggestion { Field = "url", Path = "$", Confidence = 0.8, Sample = Trim(best.Strings[0]) });
                return;
            }
        }

        // 2) A URL leaf on the item itself.
        var urlLeaf = leaves
            .Where(kv => kv.Value.Any(v => IsUrl(JsonPath.ToText(v))) && !kv.Value.All(v => IsImageUrl(JsonPath.ToText(v))))
            .Select(kv => (kv.Key, Score: NameScore(kv.Key, "url", StreamKeywords["url"], StreamKeywords) + (kv.Value.Any(v => IsStreamUrl(JsonPath.ToText(v))) ? 1 : 0)))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();
        if (urlLeaf.Key is not null)
        {
            result.StreamUrlLeaf = urlLeaf.Key;
            var fields = new Dictionary<string, FieldSpec> { ["url"] = FieldSpec.OfPath(urlLeaf.Key) };
            var sample = leaves[urlLeaf.Key].Select(JsonPath.ToText).FirstOrDefault(IsUrl);
            if (!IsStreamUrl(sample) && Regex.IsMatch(Normalize(urlLeaf.Key), "embed|iframe|player|page|watch", RegexOptions.None, TimeSpan.FromMilliseconds(50)))
            {
                fields["type"] = new FieldSpec { Value = "embed", IsShorthand = false };
            }

            result.Streams = new InlineStreamsSpec { Path = "$", Fields = fields };
            result.StreamReport.Add(new FieldSuggestion { Field = "url", Path = urlLeaf.Key, Confidence = Math.Min(1, urlLeaf.Score / 1.5), Sample = Trim(sample) });
        }
    }

    private static double StreamArrayScore(string path, List<JsonObject> elems, List<string?> strings)
    {
        var score = 0.0;
        var last = LastKey(path);
        if (StreamArrayNames.Contains(last))
        {
            score += 1.5;
        }

        var urls = elems.SelectMany(e => e.Select(kv => JsonPath.ToText(kv.Value))).Concat(strings).Where(IsUrl).ToList();
        if (urls.Count == 0)
        {
            return 0;
        }

        score += urls.Any(IsStreamUrl) ? 2 : 0.5;
        if (urls.All(IsImageUrl))
        {
            return 0;
        }

        return score;
    }

    private static Dictionary<string, FieldSpec> MapStreamFields(Dictionary<string, List<JsonNode?>> leaves, List<FieldSuggestion> report)
    {
        var fields = new Dictionary<string, FieldSpec>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in new[] { "url", "quality", "height", "bitrate", "language", "country", "type", "hd", "referer", "headers", "id", "label" })
        {
            var pick = leaves
                .Where(kv => !used.Contains(kv.Key))
                .Select(kv => (kv.Key, Score: NameScore(kv.Key, field, StreamKeywords[field], StreamKeywords) + StreamTypeScore(field, kv.Value)))
                .Where(x => x.Score >= 0.6)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (pick.Key is null)
            {
                continue;
            }

            used.Add(pick.Key);
            fields[field] = FieldSpec.OfPath(pick.Key);
            report.Add(new FieldSuggestion { Field = field, Path = pick.Key, Confidence = Math.Round(Math.Min(1, pick.Score / 1.5), 2), Sample = Trim(JsonPath.ToText(leaves[pick.Key].FirstOrDefault())) });
        }

        return fields;
    }

    private static double StreamTypeScore(string field, List<JsonNode?> values)
    {
        var texts = values.Select(JsonPath.ToText).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        if (texts.Count == 0)
        {
            return -1;
        }

        return field switch
        {
            "url" => texts.Any(IsStreamUrl) ? 1 : texts.Any(IsUrl) && !texts.All(IsImageUrl) ? 0.4 : -2,
            "quality" => texts.Any(s => StreamClassifier.HeightFromLabel(s) is not null) ? 0.5 : -0.3,
            "height" or "bitrate" => texts.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) ? 0.3 : -1,
            "language" => texts.Any(s => LocaleNormalizer.NormalizeLanguage(s) is not null) ? 0.5 : -0.5,
            "country" => texts.Any(s => LocaleNormalizer.NormalizeCountry(s) is not null) ? 0.4 : -0.5,
            "type" => texts.Any(s => StreamClassifier.Classify(s, null) != Model.StreamKind.Auto) ? 0.5 : -0.3,
            "hd" => texts.All(s => s is "true" or "false" or "0" or "1") ? 0.3 : -1,
            "referer" => texts.Any(IsUrl) ? 0.3 : -0.5,
            "label" => texts.All(IsTextualText) ? 0 : -1,
            "id" => 0,
            _ => 0,
        };
    }

    private static double NameScore(string path, string field, string[] words, Dictionary<string, string[]> all)
    {
        var segments = path.Split(new[] { '.', '[', ']', '\'' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "$" && s != "*" && !s.All(char.IsDigit)).Select(Normalize).ToList();
        if (segments.Count == 0)
        {
            return 0;
        }

        var last = segments[^1];
        var parent = segments.Count > 1 ? segments[^2] : null;
        double score = 0;
        if (words.Contains(last))
        {
            score = 1.0;
        }
        else if (words.Any(w => w.Length >= 4 && last.Contains(w, StringComparison.Ordinal)))
        {
            score = 0.6;
        }

        // "matchId", "sportKey"... are identifiers, not titles or categories.
        if (field is not ("id" or "epgId") && !words.Contains(last) && (last.EndsWith("id", StringComparison.Ordinal) || last.EndsWith("key", StringComparison.Ordinal) || last.EndsWith("code", StringComparison.Ordinal)))
        {
            score -= 0.5;
        }

        if (parent is not null && GenericLeaf.Contains(last))
        {
            if (words.Contains(parent))
            {
                score = Math.Max(score, 0.9);
            }
            else if (all.Any(kv => kv.Key != field && kv.Value.Contains(parent)))
            {
                // "$.competition.title" is the competition, not the event title.
                score *= 0.4;
            }
        }

        return score;
    }

    private static double TypeScore(string field, List<JsonNode?> values)
    {
        var texts = values.Select(JsonPath.ToText).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        if (texts.Count == 0)
        {
            return -1;
        }

        bool AllNumbers() => texts.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        return field switch
        {
            "start" or "end" => texts.All(IsDateLike) ? 0.5 : -2,
            "isLive" => texts.All(s => s is "true" or "false" or "0" or "1" || TransformEngine.DefaultTrueValues.Contains(s.ToLowerInvariant()) || s.Equals("no", StringComparison.OrdinalIgnoreCase)) ? 0.3 : -1,
            "status" => texts.Distinct(StringComparer.OrdinalIgnoreCase).Count() <= Math.Max(4, texts.Count / 2) && !AllNumbers() ? 0.2 : -0.5,
            "title" => texts.All(IsTextualText) ? 0.3 : -1.5,
            "id" => texts.Distinct(StringComparer.Ordinal).Count() == texts.Count ? 0.3 : -0.5,
            "poster" or "thumb" or "logo" => texts.Any(IsImageUrl) || texts.Any(s => s.StartsWith('/') && ImageExt.IsMatch(s)) ? 0.5 : -1.5,
            "pageUrl" => texts.All(s => IsUrl(s) && !IsImageUrl(s) && !IsStreamUrl(s)) ? 0.2 : -1.5,
            "popularity" => AllNumbers() ? 0.3 : -1.5,
            "language" => texts.Any(s => LocaleNormalizer.NormalizeLanguage(s) is not null) ? 0.4 : -1,
            "country" => texts.Any(s => LocaleNormalizer.NormalizeCountry(s) is not null) ? 0.4 : -1,
            "category" or "competition" => AllNumbers() ? -1 : 0.1,
            "description" => texts.Average(s => s.Length) > 30 ? 0.3 : -0.2,
            "number" => AllNumbers() ? 0.2 : -1,
            _ => 0,
        };
    }

    private static PaginationSpec? DetectPagination(JsonNode root, string itemsPath, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is not null && headers.TryGetValue("Link", out var link) && link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
        {
            return new PaginationSpec { Type = PaginationType.LinkHeader, MaxPages = 10 };
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        var leaves = new Dictionary<string, List<JsonNode?>>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, List<JsonArray>>(StringComparer.Ordinal);
        Flatten(obj, "$", 0, leaves, arrays);
        foreach (var (path, values) in leaves)
        {
            var key = Normalize(LastKey(path));
            var value = JsonPath.ToText(values.FirstOrDefault());
            if (key is "next" or "nextpage" or "nexturl" or "nextlink" or "nextpageurl" or "nexthref" && IsUrl(value))
            {
                return new PaginationSpec { Type = PaginationType.NextUrl, NextUrlPath = path, MaxPages = 10 };
            }

            if (key is "nextcursor" or "cursor" or "next" or "nextpagetoken" or "after" or "continuation" or "nexttoken" or "continuationtoken")
            {
                var param = key switch { "nextpagetoken" => "pageToken", "after" => "after", "continuation" or "continuationtoken" => "continuation", _ => "cursor" };
                return new PaginationSpec { Type = PaginationType.Cursor, CursorPath = path, Param = param, MaxPages = 10 };
            }
        }

        var totalPages = leaves.Keys.FirstOrDefault(p => Normalize(LastKey(p)) is "totalpages" or "pages" or "lastpage" or "pagecount" or "numpages");
        if (totalPages is not null)
        {
            return new PaginationSpec { Type = PaginationType.Page, Param = "page", Start = 1, TotalPagesPath = totalPages, MaxPages = 10 };
        }

        var hasOffset = leaves.Keys.Any(p => Normalize(LastKey(p)) is "offset" or "skip");
        var limitPath = leaves.Keys.FirstOrDefault(p => Normalize(LastKey(p)) is "limit" or "pagesize" or "perpage" or "take");
        if (hasOffset && limitPath is not null && int.TryParse(JsonPath.ToText(leaves[limitPath].FirstOrDefault()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0)
        {
            return new PaginationSpec { Type = PaginationType.Offset, Param = "offset", SizeParam = LastKey(limitPath), Size = size, MaxPages = 10 };
        }

        _ = itemsPath;
        return null;
    }

    private static string? BestKey(List<JsonObject> elems, string[] words, Func<JsonNode?, bool> valueOk)
    {
        foreach (var w in words)
        {
            var key = elems.SelectMany(e => e.Select(kv => kv.Key)).FirstOrDefault(k => Normalize(k) == w);
            if (key is not null && elems.Any(e => e.TryGetPropertyValue(key, out var v) && valueOk(v)))
            {
                return key;
            }
        }

        return null;
    }

    private static HashSet<string> Hints(string? documentation)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(documentation))
        {
            return set;
        }

        foreach (Match m in Regex.Matches(documentation, "[A-Za-z_][A-Za-z0-9_]{1,40}", RegexOptions.None, TimeSpan.FromMilliseconds(200)))
        {
            set.Add(Normalize(m.Value));
        }

        return set;
    }

    private static string Seg(string key) => Identifier.IsMatch(key) ? "." + key : "['" + key.Replace("'", "\\'", StringComparison.Ordinal) + "']";

    private static string LastKey(string path)
    {
        var parts = path.Split(new[] { '.', '[', ']', '\'' }, StringSplitOptions.RemoveEmptyEntries).Where(s => s != "$" && s != "*" && !s.All(char.IsDigit)).ToList();
        return parts.Count == 0 ? string.Empty : parts[^1];
    }

    private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool IsUrl(string? s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static bool IsImageUrl(string? s) => s is not null && (IsUrl(s) || s.StartsWith('/')) && ImageExt.IsMatch(s);

    private static bool IsStreamUrl(string? s) => IsUrl(s) && StreamExt.IsMatch(s!);

    private static bool IsTextual(JsonNode? v) => IsTextualText(JsonPath.ToText(v));

    private static bool IsTextualText(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && s.Any(char.IsLetter) && !IsUrl(s);

    private static bool IsDateLike(string s)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return (n > 1e9 && n < 2e10) || (n > 1e12 && n < 2e13);
        }

        return IsoDate.IsMatch(s) || DatePatterns.Any(p => DateTime.TryParseExact(s, p, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _));
    }

    private static string? Trim(string? s) => s is null ? null : (s.Length > 120 ? s[..120] + "…" : s);
}
