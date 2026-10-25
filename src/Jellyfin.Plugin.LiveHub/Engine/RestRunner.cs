using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Formats;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Executes the REST endpoints of a definition: iteration (per category / per date), pagination
/// (page, offset, cursor, next URL, Link header), authentication and mapping.
/// </summary>
public static class RestRunner
{
    private static readonly Regex LinkNext = new("<(?<url>[^>]+)>\\s*;[^,]*rel\\s*=\\s*\"?next\"?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Gets the id of an endpoint.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="role">The role.</param>
    /// <param name="index">The index.</param>
    /// <returns>The id.</returns>
    public static string EndpointId(EndpointSpec endpoint, string role, int index)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return !string.IsNullOrWhiteSpace(endpoint.Id) ? endpoint.Id! : (role == "categories" ? "categories" : role + index.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Runs the selected endpoints of a REST definition.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The endpoint results.</returns>
    public static async Task<List<EndpointResult>> RunAsync(ProviderRunContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var def = ctx.Definition;
        var results = new List<EndpointResult>();
        var selected = ctx.Options.EndpointIds;
        var events = (def.Events ?? new List<EndpointSpec>()).Select((e, i) => (Spec: e, Id: EndpointId(e, "events", i))).Where(e => selected is null || selected.Contains(e.Id)).ToList();
        var channels = (def.Channels ?? new List<EndpointSpec>()).Select((e, i) => (Spec: e, Id: EndpointId(e, "channels", i))).Where(e => selected is null || selected.Contains(e.Id)).ToList();

        var categories = new List<LiveCategory>();
        var needCategories = events.Concat(channels).Any(e => e.Spec.ForEach == ForEachMode.Category);
        if (def.Categories is not null && (selected is null || selected.Contains("categories") || needCategories))
        {
            var r = await RunEndpointAsync(ctx, def.Categories, "categories", "categories", categories, cancellationToken).ConfigureAwait(false);
            categories = r.Categories;
            results.Add(r);
        }

        foreach (var (spec, id) in events)
        {
            results.Add(await RunEndpointAsync(ctx, spec, "events", id, categories, cancellationToken).ConfigureAwait(false));
        }

        foreach (var (spec, id) in channels)
        {
            results.Add(await RunEndpointAsync(ctx, spec, "channels", id, categories, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Runs a single endpoint.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="role">categories | events | channels.</param>
    /// <param name="id">The endpoint id.</param>
    /// <param name="categories">Known categories (forEach category).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    public static async Task<EndpointResult> RunEndpointAsync(ProviderRunContext ctx, EndpointSpec endpoint, string role, string id, IReadOnlyList<LiveCategory> categories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(endpoint);
        var result = new EndpointResult { EndpointId = id, Role = role };
        try
        {
            foreach (var vars in Iterations(ctx, endpoint, categories))
            {
                await RunPagedAsync(ctx, endpoint, role, id, vars, result, cancellationToken).ConfigureAwait(false);
                if (result.Events.Count + result.Channels.Count + result.Categories.Count >= ctx.Options.MaxItems)
                {
                    ctx.Diagnostics.Warn($"{id}: item limit ({ctx.Options.MaxItems}) reached.");
                    break;
                }
            }

            result.Success = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderRequestException ex)
        {
            Fail(ctx, result, ex.Message);
            result.RetryAfter = ex.RetryAfter;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or EgressUnavailableException or ResponseTooLargeException or CircuitOpenException or FormatException or UriFormatException or System.Xml.XmlException)
        {
            Fail(ctx, result, ex.Message);
        }

        return result;
    }

    /// <summary>Selects the items of a body.</summary>
    /// <param name="root">The root node.</param>
    /// <param name="rootPath">The root path (null: array elements or the object itself).</param>
    /// <returns>The items.</returns>
    public static IReadOnlyList<JsonNode?> SelectItems(JsonNode? root, string? rootPath)
    {
        if (root is null)
        {
            return Array.Empty<JsonNode?>();
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return root is JsonArray arr ? arr.ToList() : new List<JsonNode?> { root };
        }

        var selected = JsonPath.Parse(rootPath).Select(root, root);
        if (selected.Count == 1 && selected[0] is JsonArray single)
        {
            return single.ToList();
        }

        return selected;
    }

    /// <summary>Fetches and parses one request of an endpoint (shared with stream chains and the wizard).</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="request">The request spec.</param>
    /// <param name="url">The rendered URL.</param>
    /// <param name="t">The template context.</param>
    /// <param name="endpointId">The endpoint id.</param>
    /// <param name="format">The declared format.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Detected format, text, JSON root and response.</returns>
    public static async Task<(BodyFormat Format, string Text, JsonNode? Json, FetchResponse Response)> FetchParsedAsync(
        ProviderRunContext ctx,
        RequestSpec request,
        Uri url,
        TemplateContext t,
        string endpointId,
        BodyFormat? format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(t);
        FetchResponse response;
        var log = new RequestLog { Endpoint = endpointId, Url = ctx.Redact(url.ToString()) };
        ctx.Diagnostics.Requests.Add(log);

        if (ctx.Options.SampleBody is { } sample)
        {
            // Wizard: the admin pasted a sample response; use it once instead of the network.
            ctx.Options.SampleBody = null;
            response = new FetchResponse { StatusCode = 200, Body = System.Text.Encoding.UTF8.GetBytes(sample), FinalUrl = url };
            log.Status = 200;
            log.Error = "sample";
        }
        else
        {
            response = await SendAsync(ctx, request, url, t, endpointId, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == 401 && ctx.Definition.Connection?.Auth?.Type == AuthType.Oauth2ClientCredentials)
            {
                AuthHandler.InvalidateToken(ctx);
                response = await SendAsync(ctx, request, url, t, endpointId, cancellationToken).ConfigureAwait(false);
            }

            log.Status = response.StatusCode;
            log.Ms = response.Elapsed.TotalMilliseconds;
            log.Bytes = response.Body.LongLength;
            log.NotModified = response.Headers.ContainsKey("X-LiveHub-NotModified");
        }

        if (!response.IsSuccess)
        {
            log.Error = "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
            throw new ProviderRequestException($"HTTP {response.StatusCode} from {ctx.Redact(url.GetLeftPart(UriPartial.Path))}")
            {
                StatusCode = response.StatusCode,
                RetryAfter = ParseRetryAfter(response, ctx.Clock),
            };
        }

        var bytes = BodyParser.MaybeGunzip(response.Body, ctx.MaxBytes * 4);
        var text = BodyParser.Decode(bytes);
        var detected = format is null or BodyFormat.Auto ? BodyParser.Detect(text, response.ContentType) : format.Value;
        JsonNode? json = null;
        if (detected == BodyFormat.Json || detected == BodyFormat.Auto)
        {
            json = BodyParser.ParseJson(text);
            detected = BodyFormat.Json;
        }

        return (detected, text, json, response);
    }

    /// <summary>Renders the URL of a request (query parameters and pagination parameters).</summary>
    /// <param name="request">The request.</param>
    /// <param name="t">The template context.</param>
    /// <param name="extra">Extra query parameters (pagination).</param>
    /// <returns>The URL.</returns>
    public static Uri BuildUrl(RequestSpec request, TemplateContext t, IReadOnlyList<(string Name, string Value)>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(t);
        var rendered = t.Render(request.Url).Trim();
        if (!Uri.TryCreate(rendered, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ProviderRequestException($"The request URL '{rendered}' is not an absolute http(s) URL (check baseUrl and the template).");
        }

        if (request.Query is not null)
        {
            foreach (var kv in request.Query)
            {
                var v = t.Render(kv.Value);
                if (!string.IsNullOrEmpty(v))
                {
                    uri = AuthHandler.AppendQuery(uri, kv.Key, v);
                }
            }
        }

        if (extra is not null)
        {
            foreach (var (name, value) in extra)
            {
                uri = AuthHandler.AppendQuery(uri, name, value);
            }
        }

        return uri;
    }

    private static async Task<FetchResponse> SendAsync(ProviderRunContext ctx, RequestSpec spec, Uri url, TemplateContext t, string endpointId, CancellationToken cancellationToken)
    {
        var request = ctx.NewRequest(url);
        request.Method = string.Equals(spec.Method, "POST", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Post : HttpMethod.Get;
        if (spec.Headers is not null)
        {
            foreach (var kv in spec.Headers)
            {
                request.Headers[kv.Key] = t.Render(kv.Value);
            }
        }

        if (request.Method == HttpMethod.Post)
        {
            request.Body = t.Render(spec.Body ?? string.Empty);
            request.ContentType = spec.ContentType ?? "application/json";
        }

        await AuthHandler.ApplyAsync(request, ctx, cancellationToken).ConfigureAwait(false);
        return await ctx.Gateway.FetchAsync(request, endpointId, conditional: !ctx.Options.Preview, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<TemplateContext> Iterations(ProviderRunContext ctx, EndpointSpec endpoint, IReadOnlyList<LiveCategory> categories)
    {
        switch (endpoint.ForEach)
        {
            case ForEachMode.Category:
                if (categories.Count == 0)
                {
                    throw new ProviderRequestException("forEach 'category' requires a working 'categories' endpoint.");
                }

                foreach (var c in categories.Take(ctx.Options.Preview ? 3 : 100))
                {
                    var t = ctx.Template.Fork();
                    t["category.id"] = c.Id;
                    t["category.name"] = c.Name;
                    t["category"] = c.Id;
                    yield return t;
                }

                break;
            case ForEachMode.Date:
                var from = endpoint.DateRange?.FromDays ?? 0;
                var to = endpoint.DateRange?.ToDays ?? 2;
                var format = string.IsNullOrWhiteSpace(endpoint.DateRange?.Format) ? "yyyy-MM-dd" : endpoint.DateRange!.Format!;
                var today = TimeZoneInfo.ConvertTime(ctx.Clock.GetUtcNow(), ctx.TimeZone).Date;
                for (var d = from; d <= to && d - from < 15; d++)
                {
                    var day = today.AddDays(d);
                    var t = ctx.Template.Fork();
                    t["date.iso"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    t["date.value"] = day.ToString(format, CultureInfo.InvariantCulture);
                    t["day"] = d.ToString(CultureInfo.InvariantCulture);
                    yield return t;
                    if (ctx.Options.Preview && d - from >= 1)
                    {
                        yield break;
                    }
                }

                break;
            default:
                yield return ctx.Template.Fork();
                break;
        }
    }

    private static async Task RunPagedAsync(ProviderRunContext ctx, EndpointSpec endpoint, string role, string id, TemplateContext vars, EndpointResult result, CancellationToken cancellationToken)
    {
        var pg = endpoint.Pagination ?? new PaginationSpec { Type = PaginationType.None };
        var maxPages = Math.Clamp(pg.MaxPages ?? 10, 1, 100);
        if (ctx.Options.Preview)
        {
            maxPages = Math.Min(maxPages, ctx.Options.PreviewMaxPages);
        }

        var page = pg.Start ?? (pg.Type == PaginationType.Offset ? 0 : 1);
        var offset = pg.Type == PaginationType.Offset ? pg.Start ?? 0 : 0;
        var size = pg.Size ?? 0;
        string? cursor = null;
        Uri? nextUrl = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var template = endpoint.Request.Url;

        for (var p = 0; p < maxPages; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = vars.Fork();
            t["page"] = page.ToString(CultureInfo.InvariantCulture);
            t["offset"] = offset.ToString(CultureInfo.InvariantCulture);
            t["limit"] = size > 0 ? size.ToString(CultureInfo.InvariantCulture) : string.Empty;
            t["cursor"] = cursor ?? string.Empty;
            if (vars["date.value"] is { } dv)
            {
                t["date"] = dv;
            }

            Uri url;
            if (nextUrl is not null)
            {
                url = nextUrl;
            }
            else
            {
                var extra = new List<(string Name, string Value)>();
                var param = pg.Param;
                switch (pg.Type)
                {
                    case PaginationType.Page when !string.IsNullOrEmpty(param) && !template.Contains("{page}", StringComparison.Ordinal):
                        extra.Add((param, page.ToString(CultureInfo.InvariantCulture)));
                        break;
                    case PaginationType.Offset when !string.IsNullOrEmpty(param) && !template.Contains("{offset}", StringComparison.Ordinal):
                        extra.Add((param, offset.ToString(CultureInfo.InvariantCulture)));
                        break;
                    case PaginationType.Cursor when cursor is not null && !template.Contains("{cursor}", StringComparison.Ordinal):
                        extra.Add((string.IsNullOrEmpty(param) ? "cursor" : param, cursor));
                        break;
                }

                if (pg.Type != PaginationType.None && size > 0 && !string.IsNullOrEmpty(pg.SizeParam) && !template.Contains("{limit}", StringComparison.Ordinal))
                {
                    extra.Add((pg.SizeParam, size.ToString(CultureInfo.InvariantCulture)));
                }

                url = BuildUrl(endpoint.Request, t, extra);
            }

            if (!seen.Add(url.AbsoluteUri))
            {
                break;
            }

            var (format, text, json, response) = await FetchParsedAsync(ctx, endpoint.Request, url, t, id, endpoint.Format, cancellationToken).ConfigureAwait(false);
            if (format == BodyFormat.M3u || format == BodyFormat.Xmltv)
            {
                FormatConverters.MapTextFormat(format, text, ctx, role, result);
                break;
            }

            var items = SelectItems(json, endpoint.Root);
            ctx.Diagnostics.Requests[^1].Items = items.Count;
            if (ctx.Options.Preview)
            {
                if (!ctx.Diagnostics.Samples.TryGetValue(id, out var samples))
                {
                    samples = new List<JsonNode?>();
                    ctx.Diagnostics.Samples[id] = samples;
                }

                samples.AddRange(items.Take(Math.Max(0, 5 - samples.Count)).Select(n => n?.DeepClone()));
            }

            foreach (var item in items)
            {
                if (result.Events.Count + result.Channels.Count + result.Categories.Count >= ctx.Options.MaxItems)
                {
                    break;
                }

                switch (role)
                {
                    case "categories":
                        if (ItemMapper.MapCategory(item, json, endpoint, ctx) is { } c && !result.Categories.Exists(x => x.Id == c.Id))
                        {
                            result.Categories.Add(c);
                        }

                        break;
                    case "channels":
                        if (ItemMapper.MapChannel(item, json, endpoint, ctx, t) is { } ch && !result.Channels.Exists(x => x.Key == ch.Key))
                        {
                            result.Channels.Add(ch);
                        }

                        break;
                    default:
                        if (ItemMapper.MapEvent(item, json, endpoint, ctx, t) is { } ev)
                        {
                            var existing = result.Events.Find(x => x.Key == ev.Key);
                            if (existing is null)
                            {
                                result.Events.Add(ev);
                            }
                            else
                            {
                                foreach (var s in ev.Streams.Where(s => !existing.Streams.Exists(x => x.Id == s.Id)))
                                {
                                    existing.Streams.Add(s);
                                }
                            }
                        }

                        break;
                }
            }

            // Next page.
            var stop = items.Count == 0;
            switch (pg.Type)
            {
                case PaginationType.None:
                    stop = true;
                    break;
                case PaginationType.Page:
                    if (pg.TotalPagesPath is { } tp && int.TryParse(JsonPath.ToText(JsonPath.Parse(tp).SelectFirst(json)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) && page >= total)
                    {
                        stop = true;
                    }

                    if (size > 0 && items.Count < size)
                    {
                        stop = true;
                    }

                    page++;
                    break;
                case PaginationType.Offset:
                    if (size > 0 && items.Count < size)
                    {
                        stop = true;
                    }

                    offset += size > 0 ? size : items.Count;
                    break;
                case PaginationType.Cursor:
                    cursor = pg.CursorPath is null ? null : JsonPath.ToText(JsonPath.Parse(pg.CursorPath).SelectFirst(json));
                    stop |= string.IsNullOrWhiteSpace(cursor) || string.Equals(cursor, "null", StringComparison.Ordinal);
                    break;
                case PaginationType.NextUrl:
                    var next = pg.NextUrlPath is null ? null : JsonPath.ToText(JsonPath.Parse(pg.NextUrlPath).SelectFirst(json));
                    var abs = TransformEngine.AbsoluteUrl(url.ToString(), next);
                    nextUrl = string.IsNullOrWhiteSpace(abs) || !Uri.TryCreate(abs, UriKind.Absolute, out var nu) ? null : nu;
                    stop |= nextUrl is null;
                    break;
                case PaginationType.LinkHeader:
                    nextUrl = ParseLinkNext(response, url);
                    stop |= nextUrl is null;
                    break;
            }

            if (stop)
            {
                break;
            }
        }
    }

    private static Uri? ParseLinkNext(FetchResponse response, Uri current)
    {
        if (!response.Headers.TryGetValue("Link", out var link))
        {
            return null;
        }

        try
        {
            var m = LinkNext.Match(link);
            return m.Success && Uri.TryCreate(current, m.Groups["url"].Value, out var next) ? next : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static TimeSpan? ParseRetryAfter(FetchResponse response, TimeProvider clock)
    {
        if (response.StatusCode is not (429 or 503) || !response.Headers.TryGetValue("Retry-After", out var value))
        {
            return response.StatusCode == 429 ? TimeSpan.FromSeconds(60) : null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600));
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
        {
            var delta = when - clock.GetUtcNow();
            return delta > TimeSpan.Zero ? delta : TimeSpan.FromSeconds(1);
        }

        return null;
    }

    private static void Fail(ProviderRunContext ctx, EndpointResult result, string message)
    {
        var safe = ctx.Redact(message);
        result.Success = false;
        result.Error = safe;
        ctx.Diagnostics.Errors.Add($"{result.EndpointId}: {safe}");
        if (ctx.Diagnostics.Requests.Count > 0 && ctx.Diagnostics.Requests[^1].Error is null)
        {
            ctx.Diagnostics.Requests[^1].Error = safe;
        }
    }
}
