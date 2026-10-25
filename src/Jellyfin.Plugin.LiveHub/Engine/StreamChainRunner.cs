using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Fetches streams that live on separate endpoints, also on several levels (event → sources → streams),
/// plus the optional event detail endpoint.
/// </summary>
public static class StreamChainRunner
{
    private const int MaxNodesPerLevel = 25;

    /// <summary>Fetches the streams of an event.</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="ev">The event (its Context provides {event.x}).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The streams.</returns>
    public static async Task<List<LiveStream>> FetchAsync(ProviderRunContext ctx, LiveEvent ev, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(ev);
        var results = new List<LiveStream>();
        var t = ctx.Template.Fork();
        t.SetAll("event.", ev.Context);

        if (ctx.Definition.EventDetail is { } detail)
        {
            try
            {
                var url = RestRunner.BuildUrl(detail.Request, t);
                var (_, _, json, _) = await RestRunner.FetchParsedAsync(ctx, detail.Request, url, t, "eventDetail", detail.Format, cancellationToken).ConfigureAwait(false);
                if (detail.Streams is not null)
                {
                    foreach (var item in RestRunner.SelectItems(json, detail.Root).Take(1))
                    {
                        var dt = t.Fork();
                        dt.Root = json;
                        foreach (var node in ItemMapper.SelectRelative(detail.Streams.Path, item))
                        {
                            Add(results, ItemMapper.MapStream(node, detail.Streams.Fields, ctx, dt, ev.ExternalId, ev.Language, ev.Country, null), ctx);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ProviderRequestException or System.Net.Http.HttpRequestException or TimeoutException or Http.SsrfException or Http.EgressUnavailableException or CircuitOpenException)
            {
                ctx.Diagnostics.Errors.Add("eventDetail: " + ctx.Redact(ex.Message));
            }
        }

        var chain = ctx.Definition.Streams;
        if (chain is not null && chain.Levels.Count > 0)
        {
            try
            {
                await RunLevelAsync(ctx, chain.Levels, 0, t, null, ev, results, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ProviderRequestException or System.Net.Http.HttpRequestException or TimeoutException or Http.SsrfException or Http.EgressUnavailableException or CircuitOpenException or FormatException)
            {
                ctx.Diagnostics.Errors.Add("streams: " + ctx.Redact(ex.Message));
            }
        }

        return results;
    }

    private static async Task RunLevelAsync(
        ProviderRunContext ctx,
        IReadOnlyList<StreamLevelSpec> levels,
        int index,
        TemplateContext t,
        LiveStream? inherited,
        LiveEvent ev,
        List<LiveStream> results,
        CancellationToken cancellationToken)
    {
        var level = levels[index];
        var url = RestRunner.BuildUrl(level.Request, t);
        var endpointId = "streams." + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (_, _, json, _) = await RestRunner.FetchParsedAsync(ctx, level.Request, url, t, endpointId, level.Format, cancellationToken).ConfigureAwait(false);
        var nodes = RestRunner.SelectItems(json, level.Root);
        ctx.Diagnostics.Requests[^1].Items = nodes.Count;
        var fields = level.Fields ?? new Dictionary<string, FieldSpec>();
        var isLast = index == levels.Count - 1;

        foreach (var node in nodes.Take(MaxNodesPerLevel))
        {
            var lt = t.Fork();
            lt.Root = json;
            if (isLast)
            {
                Add(results, ItemMapper.MapStream(node, fields, ctx, lt, ev.ExternalId, ev.Language, ev.Country, inherited), ctx);
                continue;
            }

            var name = string.IsNullOrWhiteSpace(level.As) ? "source" : level.As!;
            var child = t.Fork();
            foreach (var kv in fields)
            {
                var v = FieldExtractor.Text(kv.Value, node, lt);
                if (v is not null)
                {
                    child[name + "." + kv.Key] = v;
                }
            }

            var carried = Partial(node, fields, lt, inherited);
            await RunLevelAsync(ctx, levels, index + 1, child, carried, ev, results, cancellationToken).ConfigureAwait(false);
        }
    }

    private static LiveStream Partial(System.Text.Json.Nodes.JsonNode? node, IReadOnlyDictionary<string, FieldSpec> fields, TemplateContext t, LiveStream? inherited)
    {
        string? Text(string key) => fields.TryGetValue(key, out var s) ? FieldExtractor.Text(s, node, t) : null;
        var quality = Text("quality") ?? inherited?.QualityLabel;
        var p = new LiveStream
        {
            Label = Text("label") ?? inherited?.Label,
            Language = LocaleNormalizer.NormalizeLanguage(Text("language")) ?? inherited?.Language,
            Country = LocaleNormalizer.NormalizeCountry(Text("country")) ?? inherited?.Country,
            QualityLabel = quality,
            Height = StreamClassifier.HeightFromLabel(quality) ?? inherited?.Height,
        };
        if (inherited is not null)
        {
            foreach (var h in inherited.Headers)
            {
                p.Headers[h.Key] = h.Value;
            }
        }

        foreach (var h in FieldExtractor.Headers(fields.GetValueOrDefault("headers"), node, t))
        {
            p.Headers[h.Key] = h.Value;
        }

        if (Text("referer") is { } r)
        {
            p.Headers["Referer"] = r;
        }

        return p;
    }

    private static void Add(List<LiveStream> results, LiveStream? stream, ProviderRunContext ctx)
    {
        if (stream is not null && !results.Exists(s => s.Id == stream.Id || string.Equals(s.Url, stream.Url, StringComparison.Ordinal)))
        {
            stream.Priority = ctx.Priority;
            results.Add(stream);
        }
    }
}
