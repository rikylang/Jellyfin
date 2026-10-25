using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace LiveHub.MockServer;

/// <summary>Options for the mock server.</summary>
public sealed class MockOptions
{
    /// <summary>Gets or sets the clock used to generate relative schedules.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Gets or sets public HLS URLs to emit instead of the synthetic local ones (real playback in Jellyfin).</summary>
    public IReadOnlyList<string> PublicHlsUrls { get; set; } = Array.Empty<string>();

    /// <summary>Reads options from MOCK_PUBLIC_HLS (comma separated URLs, or "1" for the defaults).</summary>
    public static MockOptions FromEnvironment()
    {
        var o = new MockOptions();
        var env = Environment.GetEnvironmentVariable("MOCK_PUBLIC_HLS");
        if (string.Equals(env, "1", StringComparison.Ordinal) || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
        {
            o.PublicHlsUrls = DefaultPublicHls;
        }
        else if (!string.IsNullOrWhiteSpace(env))
        {
            o.PublicHlsUrls = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return o;
    }

    /// <summary>Free public HLS test streams (live first).</summary>
    public static readonly IReadOnlyList<string> DefaultPublicHls = new[]
    {
        "https://cph-p2p-msl.akamaized.net/hls/live/2000341/test/master.m3u8",
        "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8",
        "https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_ts/master.m3u8",
    };
}

/// <summary>
/// Mock server exposing six structurally different live APIs plus a synthetic HLS generator.
/// Every schedule is relative to "now", so data is always fresh.
/// </summary>
public static class MockServerApp
{
    /// <summary>API key accepted by API 2.</summary>
    public const string Api2Key = "mock-key-2";

    /// <summary>OAuth2 client id for API 3.</summary>
    public const string Api3ClientId = "livehub";

    /// <summary>OAuth2 client secret for API 3.</summary>
    public const string Api3ClientSecret = "s3cret";

    /// <summary>Bearer token issued by API 3.</summary>
    public const string Api3Token = "mock-token-3";

    /// <summary>Referer required by "referer*" HLS streams.</summary>
    public const string RequiredReferer = "https://alpha.example/";

    /// <summary>Xtream credentials.</summary>
    public const string XtreamUser = "user";

    /// <summary>Xtream password.</summary>
    public const string XtreamPass = "pass";

    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Maps all mock endpoints.</summary>
    /// <param name="app">The route builder.</param>
    /// <param name="options">Options.</param>
    public static void Map(IEndpointRouteBuilder app, MockOptions options)
    {
        var clock = options.Clock;

        app.MapGet("/", () => Results.Text("Live Hub mock server: /api1 /api2 /api3 /api4 /m3u /xtream /hls"));
        app.MapGet("/img/{file}", (string file) => Results.Bytes(Png1x1, "image/png"));

        MapApi1(app, options, clock);
        MapApi2(app, options, clock);
        MapApi3(app, options, clock);
        MapApi4(app, options, clock);
        MapApi5(app, options, clock);
        MapApi6(app, options, clock);
        MapHls(app, clock);
    }

    private static string Base(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}";

    private static string Hls(HttpContext ctx, MockOptions o, string name)
    {
        if (o.PublicHlsUrls.Count > 0)
        {
            var idx = (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0) % (uint)o.PublicHlsUrls.Count);
            return o.PublicHlsUrls[idx];
        }

        return $"{Base(ctx)}/hls/{name}/master.m3u8";
    }

    private static long Unix(DateTimeOffset t) => t.ToUnixTimeSeconds();

    private static DateTimeOffset Round(DateTimeOffset t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerMinute), t.Offset);

    // ---------------------------------------------------------------- API 1: flat array, unix seconds
    private static void MapApi1(IEndpointRouteBuilder app, MockOptions o, TimeProvider clock)
    {
        app.MapGet("/api1/events.json", (HttpContext ctx) =>
        {
            var now = Round(clock.GetUtcNow());
            var arr = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 101, ["title"] = "Inter - Milan", ["sport"] = "Calcio", ["league"] = "Serie A",
                    ["start"] = Unix(now.AddMinutes(-20)), ["live"] = 1, ["viewers"] = 15000,
                    ["img"] = "/img/inter-milan.png", ["lang"] = "ITA",
                    ["streams"] = new JsonArray
                    {
                        new JsonObject { ["url"] = Hls(ctx, o, "a101-hd"), ["q"] = "HD" },
                        new JsonObject { ["url"] = Hls(ctx, o, "a101-sd"), ["q"] = "SD", ["lang"] = "ENG" },
                    },
                },
                new JsonObject
                {
                    ["id"] = 102, ["title"] = "Roma - Lazio", ["sport"] = "Calcio", ["league"] = "Serie A",
                    ["start"] = Unix(now.AddMinutes(25)), ["live"] = 0, ["viewers"] = 9000,
                    ["img"] = "/img/roma-lazio.png", ["lang"] = "ITA",
                    ["streams"] = new JsonArray { new JsonObject { ["url"] = Hls(ctx, o, "a102"), ["q"] = "FHD" } },
                },
                new JsonObject
                {
                    ["id"] = 103, ["title"] = "Sinner - Alcaraz", ["sport"] = "Tennis", ["league"] = "ATP Finals",
                    ["start"] = Unix(now.AddHours(3)), ["live"] = 0, ["viewers"] = 7000, ["lang"] = "ENG",
                    ["streams"] = new JsonArray { new JsonObject { ["url"] = Hls(ctx, o, "a103"), ["q"] = "HD" } },
                },
                new JsonObject
                {
                    ["id"] = 104, ["title"] = "Old Match", ["sport"] = "Calcio", ["league"] = "Serie B",
                    ["start"] = Unix(now.AddHours(-6)), ["live"] = 0, ["viewers"] = 10, ["lang"] = "ITA",
                    ["streams"] = new JsonArray { new JsonObject { ["url"] = Hls(ctx, o, "a104"), ["q"] = "SD" } },
                },
            };
            return Results.Text(arr.ToJsonString(Json), "application/json");
        });
    }

    // ---------------------------------------------------------------- API 2: nested JSON, cursor pagination, ISO dates, API key, ETag
    private static void MapApi2(IEndpointRouteBuilder app, MockOptions o, TimeProvider clock)
    {
        app.MapGet("/api2/v2/categories", (HttpContext ctx) =>
        {
            if (!string.Equals(ctx.Request.Headers["X-Api-Key"], Api2Key, StringComparison.Ordinal))
            {
                return Results.StatusCode(401);
            }

            var cats = new JsonObject
            {
                ["data"] = new JsonArray
                {
                    new JsonObject { ["slug"] = "basket", ["label"] = "Basket" },
                    new JsonObject { ["slug"] = "calcio", ["label"] = "Calcio" },
                },
            };
            return Results.Text(cats.ToJsonString(Json), "application/json");
        });

        app.MapGet("/api2/v2/schedule", (HttpContext ctx, string? cursor, int? limit) =>
        {
            if (!string.Equals(ctx.Request.Headers["X-Api-Key"], Api2Key, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "missing api key" }, statusCode: 401);
            }

            var now = Round(clock.GetUtcNow());
            var us = TimeSpan.FromHours(-5);
            var all = new List<JsonObject>
            {
                Api2Item("evt-1", "Lakers vs Celtics", "basket", "Basket", "NBA", now.AddMinutes(-10).ToOffset(us), now.AddHours(2).ToOffset(us), "LIVE", "US",
                    new[] { ("Lakers", "lakers"), ("Celtics", "celtics") },
                    new[] { (Hls(ctx, o, "b1-en"), "en", "1080p", 5000000), (Hls(ctx, o, "b1-es"), "es", "720p", 3000000) }, ctx),
                Api2Item("evt-2", "Inter vs Milan", "calcio", "Calcio", "Serie A", now.AddMinutes(-15).ToOffset(TimeSpan.FromHours(1)), null, "LIVE", "IT",
                    new[] { ("Inter", "inter"), ("Milan", "milan") },
                    new[] { (Hls(ctx, o, "b2-it"), "it", "720p", 2500000) }, ctx),
                Api2Item("evt-3", "Real Madrid vs Barcelona", "calcio", "Calcio", "LaLiga", now.AddHours(2).ToOffset(TimeSpan.FromHours(1)), null, "SCHEDULED", "ES",
                    new[] { ("Real Madrid", "rma"), ("Barcelona", "fcb") },
                    new[] { (Hls(ctx, o, "b3-es"), "es", "1080p", 6000000) }, ctx),
                Api2Item("evt-4", "Bulls vs Knicks", "basket", "Basket", "NBA", now.AddHours(20).ToOffset(us), null, "SCHEDULED", "US",
                    new[] { ("Bulls", "bulls"), ("Knicks", "knicks") },
                    new[] { (Hls(ctx, o, "b4-en"), "en", "720p", 3000000) }, ctx),
                Api2Item("evt-5", "Arsenal vs Chelsea", "calcio", "Calcio", "Premier League", now.AddHours(26), null, "SCHEDULED", "GB",
                    new[] { ("Arsenal", "ars"), ("Chelsea", "che") },
                    new[] { (Hls(ctx, o, "b5-en"), "en", "1080p", 6000000) }, ctx),
            };

            var size = Math.Clamp(limit ?? 2, 1, 50);
            var start = 0;
            if (!string.IsNullOrEmpty(cursor) && cursor.StartsWith('c'))
            {
                _ = int.TryParse(cursor.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out start);
            }

            var page = all.Skip(start).Take(size).Select(i => (JsonNode)i.DeepClone()).ToArray();
            var next = start + size < all.Count ? "c" + (start + size).ToString(CultureInfo.InvariantCulture) : null;
            var body = new JsonObject
            {
                ["meta"] = new JsonObject { ["next_cursor"] = next, ["count"] = page.Length },
                ["data"] = new JsonObject { ["items"] = new JsonArray(page) },
            }.ToJsonString(Json);

            var etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16] + "\"";
            ctx.Response.Headers.ETag = etag;
            if (string.Equals(ctx.Request.Headers.IfNoneMatch, etag, StringComparison.Ordinal))
            {
                return Results.StatusCode(304);
            }

            return Results.Text(body, "application/json");
        });

        app.MapGet("/api2/openapi.json", (HttpContext ctx) =>
        {
            var spec = new JsonObject
            {
                ["openapi"] = "3.0.3",
                ["info"] = new JsonObject { ["title"] = "Mock Sports API 2", ["version"] = "2.0" },
                ["servers"] = new JsonArray { new JsonObject { ["url"] = Base(ctx) + "/api2" } },
                ["components"] = new JsonObject
                {
                    ["securitySchemes"] = new JsonObject
                    {
                        ["key"] = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = "X-Api-Key" },
                    },
                },
                ["security"] = new JsonArray { new JsonObject { ["key"] = new JsonArray() } },
                ["paths"] = new JsonObject
                {
                    ["/v2/schedule"] = new JsonObject
                    {
                        ["get"] = new JsonObject
                        {
                            ["operationId"] = "getSchedule",
                            ["summary"] = "List live and upcoming events",
                            ["parameters"] = new JsonArray
                            {
                                new JsonObject { ["name"] = "cursor", ["in"] = "query", ["schema"] = new JsonObject { ["type"] = "string" } },
                                new JsonObject { ["name"] = "limit", ["in"] = "query", ["schema"] = new JsonObject { ["type"] = "integer" } },
                            },
                        },
                    },
                    ["/v2/categories"] = new JsonObject
                    {
                        ["get"] = new JsonObject { ["operationId"] = "getCategories", ["summary"] = "List sport categories" },
                    },
                },
            };
            return Results.Text(spec.ToJsonString(Json), "application/json");
        });
    }

    private static JsonObject Api2Item(
        string id,
        string name,
        string slug,
        string label,
        string competition,
        DateTimeOffset start,
        DateTimeOffset? end,
        string state,
        string region,
        (string Name, string Badge)[] teams,
        (string Src, string Lang, string Res, int Bitrate)[] feeds,
        HttpContext ctx)
    {
        var timing = new JsonObject { ["startsAt"] = start.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) };
        if (end.HasValue)
        {
            timing["endsAt"] = end.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        return new JsonObject
        {
            ["uid"] = id,
            ["name"] = new JsonObject { ["display"] = name, ["short"] = name.Replace(" vs ", "-", StringComparison.Ordinal) },
            ["category"] = new JsonObject { ["slug"] = slug, ["label"] = label },
            ["competition"] = new JsonObject { ["title"] = competition },
            ["timing"] = timing,
            ["state"] = state,
            ["artwork"] = new JsonObject
            {
                ["poster"] = $"{Base(ctx)}/img/{id}-poster.png",
                ["thumbnail"] = $"{Base(ctx)}/img/{id}-thumb.png",
            },
            ["teams"] = new JsonArray(teams.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["badge"] = $"{Base(ctx)}/img/{t.Badge}.png" }).ToArray()),
            ["region"] = region,
            ["feeds"] = new JsonArray(feeds.Select(f => (JsonNode)new JsonObject
            {
                ["src"] = f.Src, ["audio"] = f.Lang, ["resolution"] = f.Res, ["bitrate"] = f.Bitrate,
            }).ToArray()),
        };
    }

    // ---------------------------------------------------------------- API 3: multi-level streams, OAuth2 client credentials, custom date pattern
    private static void MapApi3(IEndpointRouteBuilder app, MockOptions o, TimeProvider clock)
    {
        app.MapPost("/api3/oauth/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync().ConfigureAwait(false);
            var id = form["client_id"].ToString();
            var secret = form["client_secret"].ToString();
            var basic = ctx.Request.Headers.Authorization.ToString();
            if (basic.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])).Split(':', 2);
                id = parts[0];
                secret = parts.Length > 1 ? parts[1] : string.Empty;
            }

            if (!string.Equals(form["grant_type"], "client_credentials", StringComparison.Ordinal)
                || !string.Equals(id, Api3ClientId, StringComparison.Ordinal)
                || !string.Equals(secret, Api3ClientSecret, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            }

            return Results.Json(new { access_token = Api3Token, token_type = "Bearer", expires_in = 3600 });
        });

        bool Authorized(HttpContext ctx) =>
            string.Equals(ctx.Request.Headers.Authorization, "Bearer " + Api3Token, StringComparison.Ordinal);

        app.MapGet("/api3/matches", (HttpContext ctx) =>
        {
            if (!Authorized(ctx))
            {
                return Results.StatusCode(401);
            }

            var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
            string Kick(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, rome).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            var now = Round(clock.GetUtcNow());
            var body = new JsonObject
            {
                ["matches"] = new JsonArray
                {
                    new JsonObject { ["matchId"] = "m1", ["home"] = "Juventus", ["away"] = "Napoli", ["kickoff"] = Kick(now.AddMinutes(-30)), ["competition"] = "Serie A", ["sport"] = "football" },
                    new JsonObject { ["matchId"] = "m2", ["home"] = "Atalanta", ["away"] = "Torino", ["kickoff"] = Kick(now.AddMinutes(50)), ["competition"] = "Serie A", ["sport"] = "football" },
                },
            };
            return Results.Text(body.ToJsonString(Json), "application/json");
        });

        app.MapGet("/api3/matches/{id}/sources", (HttpContext ctx, string id) =>
        {
            if (!Authorized(ctx))
            {
                return Results.StatusCode(401);
            }

            var body = new JsonObject
            {
                ["sources"] = new JsonArray
                {
                    new JsonObject { ["sourceId"] = id + "-alpha", ["provider"] = "Alpha", ["language"] = "Italian" },
                    new JsonObject { ["sourceId"] = id + "-beta", ["provider"] = "Beta", ["language"] = "English" },
                },
            };
            return Results.Text(body.ToJsonString(Json), "application/json");
        });

        app.MapGet("/api3/sources/{sid}/streams", (HttpContext ctx, string sid) =>
        {
            if (!Authorized(ctx))
            {
                return Results.StatusCode(401);
            }

            var body = new JsonObject
            {
                ["streams"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["file"] = Hls(ctx, o, "referer-" + sid), ["label"] = "720p",
                        ["headers"] = new JsonObject { ["Referer"] = RequiredReferer },
                    },
                    new JsonObject { ["file"] = Hls(ctx, o, sid + "-sd"), ["label"] = "480p" },
                },
            };
            return Results.Text(body.ToJsonString(Json), "application/json");
        });
    }

    // ---------------------------------------------------------------- API 4: streams are embed pages
    private static void MapApi4(IEndpointRouteBuilder app, MockOptions o, TimeProvider clock)
    {
        app.MapGet("/api4/feed.json", (HttpContext ctx) =>
        {
            var now = Round(clock.GetUtcNow());
            var body = new JsonObject
            {
                ["events"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "e1", ["name"] = "Ferrari at Monza", ["category"] = "Motorsport",
                        ["date"] = now.AddMinutes(-5).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        ["embed"] = $"{Base(ctx)}/embed/e1",
                    },
                    new JsonObject
                    {
                        ["id"] = "e2", ["name"] = "Tour Stage 5", ["category"] = "Ciclismo",
                        ["date"] = now.AddHours(1).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        ["embed"] = $"{Base(ctx)}/embed/e2",
                    },
                },
            };
            return Results.Text(body.ToJsonString(Json), "application/json");
        });

        app.MapGet("/embed/{id}", (string id) =>
            Results.Content(
                $"<!doctype html><html><head><title>Embed {id}</title></head><body><div class=\"player\"><iframe src=\"/player/{id}\" allowfullscreen></iframe></div></body></html>",
                "text/html"));

        app.MapGet("/player/{id}", (HttpContext ctx, string id) =>
        {
            var url = Hls(ctx, o, "embed-" + id);
            string script;
            if (string.Equals(id, "e2", StringComparison.Ordinal))
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
                script = $"var s = atob(\"{b64}\"); jwplayer('p').setup({{ file: s }});";
            }
            else
            {
                script = "var player = new Player(" + JsonSerializer.Serialize(new { file = url }).Replace("/", "\\/", StringComparison.Ordinal) + ");";
            }

            return Results.Content($"<!doctype html><html><body><div id=\"p\"></div><script>{script}</script></body></html>", "text/html");
        });
    }

    // ---------------------------------------------------------------- API 5: M3U + XMLTV (gzip)
    private static void MapApi5(IEndpointRouteBuilder app, MockOptions o, TimeProvider clock)
    {
        app.MapGet("/m3u/playlist.m3u", (HttpContext ctx) =>
        {
            var b = Base(ctx);
            var sb = new StringBuilder();
            sb.Append("#EXTM3U url-tvg=\"").Append(b).Append("/xmltv/guide.xml.gz\"\n");
            sb.Append("#EXTINF:-1 tvg-id=\"news24.it\" tvg-name=\"News 24\" tvg-logo=\"").Append(b).Append("/img/news24.png\" group-title=\"News\" tvg-language=\"Italian\" tvg-country=\"IT\",News 24\n");
            sb.Append(Hls(ctx, o, "news24")).Append('\n');
            sb.Append("#EXTINF:-1 tvg-id=\"sport1.uk\" tvg-name=\"Sport One\" tvg-logo=\"").Append(b).Append("/img/sport1.png\" group-title=\"Sport\" tvg-language=\"English\" tvg-country=\"UK\",Sport One HD\n");
            sb.Append("#EXTVLCOPT:http-user-agent=Mozilla/5.0 LiveHubMock\n");
            sb.Append(Hls(ctx, o, "sport1")).Append('\n');
            sb.Append("#EXTINF:-1 tvg-id=\"musique.fr\" tvg-name=\"Musique\" group-title=\"Music\" tvg-language=\"fra\" tvg-country=\"FR\",Musique\n");
            sb.Append(Hls(ctx, o, "musique")).Append('\n');
            sb.Append("#EXTINF:-1 group-title=\"Adult\",Hidden channel\n");
            sb.Append(Hls(ctx, o, "hidden")).Append('\n');
            return Results.Text(sb.ToString(), "audio/x-mpegurl");
        });

        app.MapGet("/xmltv/guide.xml", (HttpContext ctx) =>
            Results.Text(BuildXmlTv(Base(ctx), clock, new[] { ("news24.it", "News 24"), ("sport1.uk", "Sport One"), ("musique.fr", "Musique") }), "application/xml"));

        app.MapGet("/xmltv/guide.xml.gz", (HttpContext ctx) =>
        {
            var xml = BuildXmlTv(Base(ctx), clock, new[] { ("news24.it", "News 24"), ("sport1.uk", "Sport One"), ("musique.fr", "Musique") });
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(xml);
                gz.Write(bytes, 0, bytes.Length);
            }

            return Results.Bytes(ms.ToArray(), "application/gzip");
        });
    }

    private static string BuildXmlTv(string baseUrl, TimeProvider clock, (string Id, string Name)[] channels)
    {
        var now = clock.GetUtcNow();
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE tv SYSTEM \"xmltv.dtd\">\n<tv generator-info-name=\"livehub-mock\">\n");
        foreach (var (id, name) in channels)
        {
            sb.Append("  <channel id=\"").Append(id).Append("\"><display-name lang=\"en\">").Append(name)
              .Append("</display-name><icon src=\"").Append(baseUrl).Append("/img/").Append(id).Append(".png\"/></channel>\n");
        }

        foreach (var (id, name) in channels)
        {
            for (var h = -2; h < 24; h++)
            {
                var s = hour.AddHours(h);
                var e = s.AddHours(1);
                sb.Append("  <programme start=\"").Append(s.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).Append(" +0000\" stop=\"")
                  .Append(e.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).Append(" +0000\" channel=\"").Append(id).Append("\">")
                  .Append("<title lang=\"en\">").Append(name).Append(" &amp; Friends ").Append(h + 2).Append("</title>")
                  .Append("<desc lang=\"en\">Programme ").Append(h + 2).Append(" on ").Append(name).Append("</desc>")
                  .Append("<category lang=\"en\">").Append(id.StartsWith("sport", StringComparison.Ordinal) ? "Sports" : "News").Append("</category>")
                  .Append("</programme>\n");
            }
        }

        sb.Append("</tv>\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- API 6: Xtream Codes
    private static void MapApi6(IEndpointRouteBuilder app, MockOptions o, TimeProvider clock)
    {
        app.MapGet("/xtream/player_api.php", (HttpContext ctx, string? username, string? password, string? action, string? stream_id) =>
        {
            var ok = string.Equals(username, XtreamUser, StringComparison.Ordinal) && string.Equals(password, XtreamPass, StringComparison.Ordinal);
            if (!ok)
            {
                return Results.Text("{\"user_info\":{\"auth\":0}}", "application/json");
            }

            var b = Base(ctx);
            var now = clock.GetUtcNow();
            switch (action)
            {
                case null or "":
                    return Results.Text(new JsonObject
                    {
                        ["user_info"] = new JsonObject
                        {
                            ["username"] = XtreamUser, ["password"] = XtreamPass, ["auth"] = 1, ["status"] = "Active",
                            ["exp_date"] = "1893456000", ["max_connections"] = "2",
                            ["allowed_output_formats"] = new JsonArray("m3u8", "ts"),
                        },
                        ["server_info"] = new JsonObject
                        {
                            ["url"] = ctx.Request.Host.Host, ["port"] = (ctx.Request.Host.Port ?? 80).ToString(CultureInfo.InvariantCulture),
                            ["server_protocol"] = ctx.Request.Scheme, ["timezone"] = "Europe/Rome", ["timestamp_now"] = Unix(now),
                        },
                    }.ToJsonString(Json), "application/json");
                case "get_live_categories":
                    return Results.Text("[{\"category_id\":\"1\",\"category_name\":\"Sport IT\",\"parent_id\":0},{\"category_id\":\"2\",\"category_name\":\"Cinema\",\"parent_id\":0},{\"category_id\":\"3\",\"category_name\":\"XXX Adult\",\"parent_id\":0}]", "application/json");
                case "get_live_streams":
                    var arr = new JsonArray
                    {
                        XtStream(1, "Sport Italia HD", 1001, "sportitalia.it", "1", b),
                        XtStream(2, "Calcio 24", 1002, "calcio24.it", "1", b),
                        XtStream(3, "Cine Uno", 1003, "cineuno.it", "2", b),
                        XtStream(4, "Adult One", 1004, string.Empty, "3", b),
                    };
                    return Results.Text(arr.ToJsonString(Json), "application/json");
                case "get_short_epg":
                    var listings = new JsonArray();
                    var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
                    for (var i = 0; i < 4; i++)
                    {
                        var s = hour.AddHours(i);
                        listings.Add(new JsonObject
                        {
                            ["id"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                            ["title"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Show " + i)),
                            ["description"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Description " + i)),
                            ["lang"] = "it",
                            ["start"] = s.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                            ["end"] = s.AddHours(1).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                            ["start_timestamp"] = Unix(s).ToString(CultureInfo.InvariantCulture),
                            ["stop_timestamp"] = Unix(s.AddHours(1)).ToString(CultureInfo.InvariantCulture),
                            ["channel_id"] = stream_id,
                        });
                    }

                    return Results.Text(new JsonObject { ["epg_listings"] = listings }.ToJsonString(Json), "application/json");
                default:
                    return Results.Text("[]", "application/json");
            }
        });

        app.MapGet("/xtream/live/{user}/{pass}/{file}", (HttpContext ctx, string user, string pass, string file) =>
        {
            if (!string.Equals(user, XtreamUser, StringComparison.Ordinal) || !string.Equals(pass, XtreamPass, StringComparison.Ordinal))
            {
                return Results.StatusCode(403);
            }

            var id = Path.GetFileNameWithoutExtension(file);
            return Results.Redirect(Hls(ctx, o, "xt" + id));
        });

        app.MapGet("/xtream/xmltv.php", (HttpContext ctx, string? username, string? password) =>
        {
            if (!string.Equals(username, XtreamUser, StringComparison.Ordinal) || !string.Equals(password, XtreamPass, StringComparison.Ordinal))
            {
                return Results.StatusCode(403);
            }

            return Results.Text(BuildXmlTv(Base(ctx), clock, new[] { ("sportitalia.it", "Sport Italia"), ("calcio24.it", "Calcio 24"), ("cineuno.it", "Cine Uno") }), "application/xml");
        });
    }

    private static JsonObject XtStream(int num, string name, int id, string epg, string cat, string b) => new()
    {
        ["num"] = num, ["name"] = name, ["stream_type"] = "live", ["stream_id"] = id,
        ["stream_icon"] = $"{b}/img/xt{id}.png", ["epg_channel_id"] = epg, ["added"] = "1700000000",
        ["category_id"] = cat, ["custom_sid"] = string.Empty, ["tv_archive"] = 0,
    };

    // ---------------------------------------------------------------- Synthetic HLS
    private static void MapHls(IEndpointRouteBuilder app, TimeProvider clock)
    {
        app.MapGet("/hls/{name}/master.m3u8", (HttpContext ctx, string name) =>
        {
            if (Denied(ctx, name))
            {
                return Results.StatusCode(403);
            }

            if (name.Contains("single", StringComparison.Ordinal))
            {
                return Results.Text(MediaPlaylist(clock, name, "single"), "application/vnd.apple.mpegurl");
            }

            const string Master = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-INDEPENDENT-SEGMENTS\n"
                + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,AVERAGE-BANDWIDTH=4500000,RESOLUTION=1920x1080,CODECS=\"avc1.640028,mp4a.40.2\",FRAME-RATE=25.000\n1080/index.m3u8\n"
                + "#EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=854x480,CODECS=\"avc1.4d401e,mp4a.40.2\"\n480/index.m3u8\n";
            return Results.Text(Master, "application/vnd.apple.mpegurl");
        });

        app.MapGet("/hls/{name}/{variant}/index.m3u8", (HttpContext ctx, string name, string variant) =>
            Denied(ctx, name) ? Results.StatusCode(403) : Results.Text(MediaPlaylist(clock, name, variant), "application/vnd.apple.mpegurl"));

        app.MapGet("/hls/{name}/{variant}/{segment}", (HttpContext ctx, string name, string variant, string segment) =>
        {
            if (Denied(ctx, name) || name.StartsWith("brokenseg", StringComparison.Ordinal))
            {
                return Results.StatusCode(name.StartsWith("brokenseg", StringComparison.Ordinal) ? 503 : 403);
            }

            var payload = Encoding.ASCII.GetBytes($"LIVEHUB-SEG|{name}|{variant}|{segment}|");
            var data = new byte[188 * 8];
            for (var i = 0; i < data.Length; i += 188)
            {
                data[i] = 0x47; // MPEG-TS sync byte
            }

            Array.Copy(payload, 0, data, 4, Math.Min(payload.Length, 180));
            return Results.Bytes(data, "video/mp2t");
        });
    }

    private static bool Denied(HttpContext ctx, string name)
    {
        if (name.StartsWith("forbidden", StringComparison.Ordinal))
        {
            return true;
        }

        return name.StartsWith("referer", StringComparison.Ordinal)
            && !string.Equals(ctx.Request.Headers.Referer, RequiredReferer, StringComparison.Ordinal);
    }

    private static string MediaPlaylist(TimeProvider clock, string name, string variant)
    {
        const int Target = 4;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var seq = name.StartsWith("dead", StringComparison.Ordinal) ? 1000 : (now / Target) - 5;
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:").Append(Target).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append(seq).Append('\n');
        for (var i = 0; i < 6; i++)
        {
            sb.Append("#EXTINF:4.000,\n");
            if (string.Equals(variant, "single", StringComparison.Ordinal))
            {
                sb.Append("single/");
            }

            sb.Append("seg").Append(seq + i).Append(".ts\n");
        }

        return sb.ToString();
    }
}
