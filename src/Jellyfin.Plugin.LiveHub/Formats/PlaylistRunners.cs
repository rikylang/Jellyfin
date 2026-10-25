using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Formats;

/// <summary>
/// Runners for non-REST kinds: M3U (+XMLTV), Xtream Codes and direct streams.
/// </summary>
public static class PlaylistRunners
{
    /// <summary>Runs an M3U definition.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The endpoint results.</returns>
    public static async Task<List<EndpointResult>> RunM3uAsync(ProviderRunContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var spec = ctx.Definition.M3u ?? throw new ProviderRequestException("Missing 'm3u' section.");
        var results = new List<EndpointResult>();
        var playlistResult = new EndpointResult { EndpointId = "playlist", Role = "channels" };
        results.Add(playlistResult);
        M3uPlaylist? playlist = null;
        try
        {
            var text = await FetchTextAsync(ctx, ctx.Template.Render(spec.Url), "playlist", cancellationToken).ConfigureAwait(false);
            if (M3uParser.IsHlsPlaylist(text))
            {
                throw new ProviderRequestException("This URL is an HLS stream, not a channel playlist: use kind 'direct'.");
            }

            playlist = M3uParser.Parse(text);
            if (playlist.Entries.Count == 0)
            {
                throw new ProviderRequestException("The playlist contains no entries.");
            }

            if (spec.Mode == M3uMode.Events)
            {
                playlistResult.Role = "events";
                playlistResult.Events.AddRange(FormatConverters.M3uToEvents(playlist, ctx, spec));
            }
            else
            {
                playlistResult.Channels.AddRange(FormatConverters.M3uToChannels(playlist, ctx, spec));
            }

            playlistResult.Success = true;
        }
        catch (Exception ex) when (IsExpected(ex, cancellationToken))
        {
            Fail(ctx, playlistResult, ex.Message);
            return results;
        }

        var guideUrl = !string.IsNullOrWhiteSpace(spec.XmltvUrl) ? ctx.Template.Render(spec.XmltvUrl) : playlist?.GuideUrl;
        if (!string.IsNullOrWhiteSpace(guideUrl) && playlistResult.Channels.Count > 0)
        {
            results.Add(await RunGuideAsync(ctx, guideUrl, playlistResult.Channels, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Runs an Xtream Codes definition.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The endpoint results.</returns>
    public static async Task<List<EndpointResult>> RunXtreamAsync(ProviderRunContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var spec = ctx.Definition.Xtream ?? throw new ProviderRequestException("Missing 'xtream' section.");
        var server = ctx.Template.Render(spec.Server).TrimEnd('/');
        var user = ctx.Template.Render(spec.Username);
        var pass = ctx.Template.Render(spec.Password);
        var api = $"{server}/player_api.php?username={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pass)}";
        var results = new List<EndpointResult>();
        var live = new EndpointResult { EndpointId = "live_streams", Role = "channels" };
        results.Add(live);
        try
        {
            var info = BodyParser.ParseJson(await FetchTextAsync(ctx, api, "account", cancellationToken).ConfigureAwait(false));
            var auth = JsonPath.ToText(info?["user_info"]?["auth"]);
            if (!string.Equals(auth, "1", StringComparison.Ordinal) && !string.Equals(auth, "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderRequestException("Xtream: invalid credentials or inactive account.");
            }

            var status = JsonPath.ToText(info?["user_info"]?["status"]);
            if (!string.IsNullOrEmpty(status) && !string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Diagnostics.Warn("Xtream account status: " + status);
            }

            var categories = new Dictionary<string, string>(StringComparer.Ordinal);
            var catJson = BodyParser.ParseJson(await FetchTextAsync(ctx, api + "&action=get_live_categories", "categories", cancellationToken).ConfigureAwait(false)) as JsonArray;
            foreach (var c in catJson ?? new JsonArray())
            {
                var id = JsonPath.ToText(c?["category_id"]);
                var name = JsonPath.ToText(c?["category_name"]);
                if (id is not null && name is not null)
                {
                    categories[id] = name;
                    live.Categories.Add(new LiveCategory { ProviderId = ctx.ProviderId, Id = id, Name = name });
                }
            }

            var streams = BodyParser.ParseJson(await FetchTextAsync(ctx, api + "&action=get_live_streams", "live_streams", cancellationToken).ConfigureAwait(false)) as JsonArray;
            var ext = spec.Output == XtreamOutput.Ts ? "ts" : "m3u8";
            var def = ctx.Definition;
            foreach (var s in streams ?? new JsonArray())
            {
                var streamId = JsonPath.ToText(s?["stream_id"]);
                var name = JsonPath.ToText(s?["name"])?.Trim();
                if (string.IsNullOrEmpty(streamId) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var catId = JsonPath.ToText(s?["category_id"]) ?? string.Empty;
                var catName = categories.GetValueOrDefault(catId);
                if (!FormatConverters.Accept(catName, spec.IncludeCategories, spec.ExcludeCategories))
                {
                    continue;
                }

                var language = LocaleNormalizer.NormalizeLanguage(def.Defaults?.Language) ?? LocaleNormalizer.GuessLanguage(name) ?? LocaleNormalizer.GuessLanguage(catName);
                var country = LocaleNormalizer.GuessCountry(name) ?? LocaleNormalizer.GuessCountry(catName) ?? LocaleNormalizer.NormalizeCountry(def.Defaults?.Country) ?? LocaleNormalizer.CountryForLanguage(language);
                var ch = new LiveChannel
                {
                    ProviderId = ctx.ProviderId,
                    ExternalId = streamId,
                    Key = Keys.Channel(ctx.ProviderId, streamId),
                    Name = name,
                    Number = JsonPath.ToText(s?["num"]),
                    Category = catName ?? def.Defaults?.Category,
                    LogoUrl = NullIfEmpty(JsonPath.ToText(s?["stream_icon"])),
                    EpgId = NullIfEmpty(JsonPath.ToText(s?["epg_channel_id"])),
                    Language = language,
                    Country = country,
                    ContentRating = def.Defaults?.ContentRating,
                    Priority = ctx.Priority,
                };
                var url = $"{server}/live/{Uri.EscapeDataString(user)}/{Uri.EscapeDataString(pass)}/{streamId}.{ext}";
                var height = StreamClassifier.HeightFromLabel(name);
                ch.Streams.Add(new LiveStream
                {
                    Id = Keys.Stream(ctx.ProviderId, streamId, streamId),
                    ProviderId = ctx.ProviderId,
                    Url = url,
                    Kind = spec.Output == XtreamOutput.Ts ? StreamKind.Ts : StreamKind.Hls,
                    Height = height,
                    QualityLabel = StreamClassifier.LabelFromHeight(height),
                    Language = language,
                    Country = country,
                    Label = def.Name,
                    Priority = ctx.Priority,
                });
                live.Channels.Add(ch);
                if (live.Channels.Count >= ctx.Options.MaxItems)
                {
                    ctx.Diagnostics.Warn($"Xtream list truncated at {ctx.Options.MaxItems} channels.");
                    break;
                }
            }

            live.Success = true;
        }
        catch (Exception ex) when (IsExpected(ex, cancellationToken))
        {
            Fail(ctx, live, ex.Message);
            return results;
        }

        if (spec.IncludeEpg != false && live.Channels.Count > 0)
        {
            var epgUrl = $"{server}/xmltv.php?username={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pass)}";
            results.Add(await RunGuideAsync(ctx, epgUrl, live.Channels, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Runs a direct stream definition.</summary>
    /// <param name="ctx">The context.</param>
    /// <returns>The endpoint results.</returns>
    public static List<EndpointResult> RunDirect(ProviderRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var spec = ctx.Definition.Direct ?? throw new ProviderRequestException("Missing 'direct' section.");
        var result = new EndpointResult { EndpointId = "direct", Role = "channels" };
        var url = ctx.Template.Render(spec.Url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Fail(ctx, result, "The direct URL is not an absolute http(s) URL.");
            return new List<EndpointResult> { result };
        }

        var def = ctx.Definition;
        var language = LocaleNormalizer.NormalizeLanguage(def.Defaults?.Language);
        var country = LocaleNormalizer.NormalizeCountry(def.Defaults?.Country) ?? LocaleNormalizer.CountryForLanguage(language);
        var name = string.IsNullOrWhiteSpace(spec.Name) ? def.Name : spec.Name;
        var ch = new LiveChannel
        {
            ProviderId = ctx.ProviderId,
            ExternalId = "direct",
            Key = Keys.Channel(ctx.ProviderId, "direct"),
            Name = name,
            Category = spec.Category ?? def.Defaults?.Category,
            LogoUrl = spec.Logo,
            Language = language,
            Country = country,
            ContentRating = def.Defaults?.ContentRating,
            Priority = ctx.Priority,
        };
        var stream = new LiveStream
        {
            Id = Keys.Stream(ctx.ProviderId, "direct", url),
            ProviderId = ctx.ProviderId,
            Url = url,
            Kind = spec.Type is null or StreamTypeSpec.Auto ? StreamClassifier.FromUrl(url) : (StreamKind)(int)spec.Type.Value,
            Language = language,
            Country = country,
            Label = def.Name,
            Priority = ctx.Priority,
        };
        foreach (var h in spec.Headers ?? new Dictionary<string, string>())
        {
            stream.Headers[h.Key] = ctx.Template.Render(h.Value);
        }

        ch.Streams.Add(stream);
        result.Channels.Add(ch);
        result.Success = true;
        return new List<EndpointResult> { result };
    }

    private static async Task<EndpointResult> RunGuideAsync(ProviderRunContext ctx, string url, List<LiveChannel> channels, CancellationToken cancellationToken)
    {
        var result = new EndpointResult { EndpointId = "xmltv", Role = "programs" };
        try
        {
            var xml = await FetchTextAsync(ctx, url, "xmltv", cancellationToken, Math.Max(ctx.MaxBytes, 64L * 1024 * 1024)).ConfigureAwait(false);
            var now = ctx.Clock.GetUtcNow();
            // When every channel has an EPG id the parser keeps only those (low memory); otherwise name matching needs all.
            var filter = channels.TrueForAll(c => c.EpgId is not null)
                ? new HashSet<string>(channels.Select(c => c.EpgId!), StringComparer.OrdinalIgnoreCase)
                : null;
            var guide = XmlTvParser.Parse(xml, now - FormatConverters.GuidePast, now + FormatConverters.GuideFuture, filter, ctx.Options.Preview ? 2000 : 200_000);
            result.Programs.AddRange(FormatConverters.XmlTvToPrograms(guide, channels));
            result.Success = true;
        }
        catch (Exception ex) when (IsExpected(ex, cancellationToken))
        {
            Fail(ctx, result, "XMLTV: " + ex.Message);
        }

        return result;
    }

    private static async Task<string> FetchTextAsync(ProviderRunContext ctx, string url, string endpointId, CancellationToken cancellationToken, long? maxBytes = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ProviderRequestException($"Invalid URL for '{endpointId}'.");
        }

        var log = new RequestLog { Endpoint = endpointId, Url = ctx.Redact(url) };
        ctx.Diagnostics.Requests.Add(log);
        if (ctx.Options.SampleBody is { } sample)
        {
            ctx.Options.SampleBody = null;
            log.Status = 200;
            log.Error = "sample";
            return sample;
        }

        var request = ctx.NewRequest(uri);
        if (maxBytes.HasValue)
        {
            request.MaxBytes = maxBytes.Value;
        }

        if (request.Timeout < TimeSpan.FromSeconds(30) && endpointId == "xmltv")
        {
            request.Timeout = TimeSpan.FromSeconds(60);
        }

        await AuthHandler.ApplyAsync(request, ctx, cancellationToken).ConfigureAwait(false);
        var response = await ctx.Gateway.FetchAsync(request, endpointId, conditional: !ctx.Options.Preview, cancellationToken).ConfigureAwait(false);
        log.Status = response.StatusCode;
        log.Ms = response.Elapsed.TotalMilliseconds;
        log.Bytes = response.Body.LongLength;
        log.NotModified = response.Headers.ContainsKey("X-LiveHub-NotModified");
        if (!response.IsSuccess)
        {
            log.Error = "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
            throw new ProviderRequestException($"HTTP {response.StatusCode} for '{endpointId}'.") { StatusCode = response.StatusCode };
        }

        return BodyParser.Decode(BodyParser.MaybeGunzip(response.Body, (maxBytes ?? ctx.MaxBytes) * 8));
    }

    private static bool IsExpected(Exception ex, CancellationToken cancellationToken)
        => !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
           && ex is ProviderRequestException or System.Net.Http.HttpRequestException or TimeoutException or Http.SsrfException
               or Http.EgressUnavailableException or Http.ResponseTooLargeException or CircuitOpenException or System.Xml.XmlException
               or FormatException or InvalidOperationException or System.IO.InvalidDataException;

    private static void Fail(ProviderRunContext ctx, EndpointResult result, string message)
    {
        var safe = ctx.Redact(message);
        result.Success = false;
        result.Error = safe;
        ctx.Diagnostics.Errors.Add($"{result.EndpointId}: {safe}");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
