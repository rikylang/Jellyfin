using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Security;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>
/// "Test playback" of the wizard/editor: finds the stream (lazy chains included), runs the resolver chain and checks
/// that a media playlist and a first segment can actually be downloaded through the egress, with a diagnosis.
/// </summary>
public sealed class PlaybackTester
{
    private readonly ProviderEngine _engine;
    private readonly DefinitionStore _store;
    private readonly StreamResolverService _resolver;
    private readonly IOutboundHttp _http;
    private readonly LocalizationService _loc;
    private readonly ConfigAccessor _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackTester"/> class.
    /// </summary>
    /// <param name="engine">Engine.</param>
    /// <param name="store">Definitions.</param>
    /// <param name="resolver">Resolver.</param>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="loc">Localization.</param>
    /// <param name="config">Configuration.</param>
    public PlaybackTester(ProviderEngine engine, DefinitionStore store, StreamResolverService resolver, IOutboundHttp http, LocalizationService loc, ConfigAccessor config)
    {
        _engine = engine;
        _store = store;
        _resolver = resolver;
        _http = http;
        _loc = loc;
        _config = config;
    }

    /// <summary>Runs the test.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    public async Task<PlaybackTestResult> TestAsync(PlaybackTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lang = LocalizationService.Normalize(_config.Current.UiLanguage is "it" or "en" ? _config.Current.UiLanguage : _config.Current.DefaultLanguage);
        var result = new PlaybackTestResult();
        var sw = Stopwatch.StartNew();
        try
        {
            var definition = LoadDefinition(request);
            var stream = await FindStreamAsync(request, definition, result, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                result.Summary = _loc.Get(lang, "hint.noStream");
                return result;
            }

            var resolved = await _resolver.ResolveAsync(stream, definition, true, result.Steps, cancellationToken).ConfigureAwait(false);
            result.Strategy = resolved.Strategy;
            result.FromEmbed = resolved.FromEmbed;
            result.Format = resolved.Kind.ToString().ToUpperInvariant();
            result.ResolvedUrl = Redactor.Redact(resolved.Url.AbsoluteUri, definition?.Secrets?.Values);
            result.Egress = resolved.EgressProfileId;
            foreach (var kv in Redactor.RedactHeaders(resolved.Headers))
            {
                result.RequiredHeaders[kv.Key] = kv.Value;
            }

            if (resolved.Kind == StreamKind.Hls)
            {
                await CheckHlsAsync(resolved, result, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result.Playable = true;
                result.Steps.Add("stream: " + resolved.Kind + " " + (resolved.ContentType ?? string.Empty));
            }

            result.LatencyMs = sw.Elapsed.TotalMilliseconds;
            result.Summary = result.Playable
                ? _loc.Format(lang, "playback.okSummary", result.Format ?? "?", (int)result.LatencyMs)
                : (result.Summary.Length > 0 ? result.Summary : _loc.Get(lang, "hint.noStream"));
        }
        catch (EgressUnavailableException ex)
        {
            result.Steps.Add("egress: " + ex.Message);
            result.Summary = _loc.Get(lang, "hint.egress");
        }
        catch (ResolveException ex)
        {
            result.Steps.Add("error: " + ex.Message);
            result.Summary = _loc.Get(lang, ex.Hint ?? "hint.noStream");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or ResponseTooLargeException or ArgumentException or InvalidOperationException)
        {
            result.Steps.Add("error: " + ex.Message);
            result.Summary = _loc.Get(lang, ex is SsrfException ? "hint.ssrf" : ex is TimeoutException ? "hint.timeout" : "hint.network");
        }

        result.LatencyMs = sw.Elapsed.TotalMilliseconds;
        return result;
    }

    private ProviderDefinition? LoadDefinition(PlaybackTestRequest request)
    {
        if (request.Definition is not null)
        {
            return _store.WithPlainSecrets(_store.RestoreMaskedSecrets(DefinitionSerializer.Clone(request.Definition)));
        }

        return string.IsNullOrEmpty(request.ProviderId) ? null : _store.GetForRun(request.ProviderId);
    }

    private async Task<LiveStream?> FindStreamAsync(PlaybackTestRequest request, ProviderDefinition? definition, PlaybackTestResult result, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            return new LiveStream
            {
                Id = "test",
                ProviderId = definition?.Id ?? string.Empty,
                Url = request.Url,
                Kind = StreamClassifier.FromUrl(request.Url),
            };
        }

        if (definition is null)
        {
            throw new ArgumentException("A definition, a provider id or a URL is required.");
        }

        var gateway = new ProviderGateway(definition.Id + "#test", _http, _engine.Clock) { BypassBreaker = true };
        var run = await _engine.RunAsync(definition, new RunOptions { Preview = true, MaxItems = 1000 }, cancellationToken, gateway).ConfigureAwait(false);
        result.Steps.Add($"provider: {run.Snapshot.Events.Count} event(s), {run.Snapshot.Channels.Count} channel(s)");
        List<LiveStream> streams;
        if (request.IsChannel)
        {
            var channel = run.Snapshot.Channels.Find(c => c.Key == request.ItemKey) ?? run.Snapshot.Channels.FirstOrDefault();
            streams = channel?.Streams ?? new List<LiveStream>();
        }
        else
        {
            var ev = run.Snapshot.Events.Find(e => e.Key == request.ItemKey) ?? run.Snapshot.Events.FirstOrDefault();
            if (ev is null)
            {
                return null;
            }

            streams = ev.Streams.ToList();
            if (ev.HasLazyStreams || streams.Count == 0)
            {
                var ctx = _engine.CreateContext(definition, new RunOptions { Preview = true }, gateway);
                var lazy = await StreamChainRunner.FetchAsync(ctx, ev, cancellationToken).ConfigureAwait(false);
                result.Steps.Add($"stream chain: {lazy.Count} stream(s)");
                result.Steps.AddRange(ctx.Diagnostics.Errors.Take(3).Select(e => "stream chain: " + e));
                streams.AddRange(lazy.Where(s => !streams.Exists(x => x.Id == s.Id)));
            }
        }

        return streams.Find(s => s.Id == request.StreamId) ?? streams.FirstOrDefault();
    }

    private async Task CheckHlsAsync(ResolvedStream resolved, PlaybackTestResult result, CancellationToken cancellationToken)
    {
        var playlist = resolved.Playlist!;
        Uri mediaUrl = resolved.Url;
        if (playlist.IsMaster)
        {
            foreach (var v in playlist.Variants.OrderByDescending(v => v.Bandwidth))
            {
                result.Variants.Add(v.Describe);
                foreach (var c in (v.Codecs ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!result.Codecs.Contains(c))
                    {
                        result.Codecs.Add(c);
                    }
                }
            }

            var chosen = HlsPlaylist.Choose(playlist.Variants, VariantSelector.Auto);
            if (chosen is null)
            {
                result.Summary = "Empty master playlist";
                return;
            }

            mediaUrl = chosen.Uri;
            var media = await FetchAsync(resolved, mediaUrl, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (!media.IsSuccess)
            {
                result.Steps.Add($"media playlist: HTTP {media.StatusCode}");
                result.Summary = _loc.Get(LocalizationService.Normalize(_config.Current.UiLanguage), DirectResolver.HintFor(media.StatusCode));
                return;
            }

            playlist = HlsPlaylist.Parse(media.Text(), media.FinalUrl);
        }

        result.Steps.Add($"media playlist: {playlist.Segments.Count} segment(s), target {playlist.TargetDuration:0}s{(playlist.Ended ? ", ended (VOD/replay)" : ", live")}");
        var segment = playlist.Segments.Count > 3 ? playlist.Segments[^3] : playlist.Segments.FirstOrDefault();
        if (segment is null)
        {
            result.Summary = "No segments in the playlist";
            return;
        }

        var data = await FetchAsync(resolved, segment.Uri, 64L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (!data.IsSuccess)
        {
            result.Steps.Add($"segment: HTTP {data.StatusCode}");
            result.Summary = _loc.Get(LocalizationService.Normalize(_config.Current.UiLanguage), DirectResolver.HintFor(data.StatusCode));
            return;
        }

        var kind = DirectResolver.Sniff(data.Body, data.ContentType);
        var container = kind switch
        {
            StreamKind.Ts => "MPEG-TS",
            StreamKind.Mp4 => "fMP4",
            _ => data.Body.Length > 3 && data.Body[0] == 'I' && data.Body[1] == 'D' && data.Body[2] == '3' ? "ID3/AAC" : data.ContentType ?? "?",
        };
        result.Steps.Add($"segment: {data.Body.Length / 1024} KB, {container}");
        result.Format = "HLS · " + container;
        result.Playable = data.Body.Length > 0 && kind != StreamKind.Embed;
    }

    private Task<FetchResponse> FetchAsync(ResolvedStream resolved, Uri url, long maxBytes, CancellationToken cancellationToken)
        => _http.FetchAsync(
            new FetchRequest
            {
                ProviderId = resolved.ProviderId,
                Url = url,
                Headers = new Dictionary<string, string>(resolved.Headers, StringComparer.OrdinalIgnoreCase),
                EgressProfileId = resolved.EgressProfileId,
                AllowPrivateNetwork = resolved.AllowPrivateNetwork,
                MaxBytes = maxBytes,
                Timeout = TimeSpan.FromSeconds(15),
            },
            cancellationToken);
}
