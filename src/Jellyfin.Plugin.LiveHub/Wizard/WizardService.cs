using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Formats;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Wizard;

/// <summary>
/// "Add API" wizard: probes a URL, recognises the format, proposes a definition and previews what it extracts.
/// </summary>
public sealed class WizardService
{
    private const long ProbeBytes = 4 * 1024 * 1024;

    private readonly ProviderEngine _engine;
    private readonly IOutboundHttp _http;
    private readonly DefinitionStore _store;
    private readonly ILogger<WizardService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WizardService"/> class.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="store">The definition store.</param>
    /// <param name="logger">Logger.</param>
    public WizardService(ProviderEngine engine, IOutboundHttp http, DefinitionStore store, ILogger<WizardService> logger)
    {
        _engine = engine;
        _http = http;
        _store = store;
        _logger = logger;
    }

    /// <summary>Detects the format of a source and proposes a definition.</summary>
    /// <param name="input">The input.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The detection.</returns>
    public async Task<WizardDetection> DetectAsync(WizardInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var detection = new WizardDetection();

        // OpenAPI pasted by the admin.
        if (!string.IsNullOrWhiteSpace(input.OpenApiSpec) && OpenApiParser.Parse(input.OpenApiSpec) is { } spec)
        {
            return await FromOpenApiAsync(spec, input, detection, cancellationToken).ConfigureAwait(false);
        }

        Uri? uri = null;
        if (!string.IsNullOrWhiteSpace(input.Url) && (!Uri.TryCreate(input.Url.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            detection.Warnings.Add("The URL must be an absolute http(s) URL.");
            return detection;
        }

        string text;
        string? contentType = null;
        byte[] head;
        IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(input.SampleResponse))
        {
            text = input.SampleResponse;
            head = Encoding.UTF8.GetBytes(text.Length > 2048 ? text[..2048] : text);
        }
        else if (uri is not null)
        {
            try
            {
                (head, text, contentType, headers, detection.HttpStatus) = await ProbeAsync(uri, input, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or SsrfException or EgressUnavailableException or ProviderRequestException)
            {
                detection.Warnings.Add("Unable to download a sample: " + Redactor.Redact(ex.Message, input.Secrets?.Values));
                return detection;
            }

            if (detection.HttpStatus is >= 400)
            {
                detection.Warnings.Add($"The server answered HTTP {detection.HttpStatus}: check URL and credentials.");
                if (detection.HttpStatus is 401 or 403)
                {
                    return detection;
                }
            }
        }
        else
        {
            detection.Warnings.Add("Enter a URL or paste a sample response.");
            return detection;
        }

        detection.Sample = text.Length > 2000 ? text[..2000] + "…" : text;
        detection.Format = DetectFormat(uri, head, text, contentType);
        switch (detection.Format)
        {
            case SourceFormat.OpenApi when OpenApiParser.Parse(text) is { } fetched:
                return await FromOpenApiAsync(fetched, input, detection, cancellationToken).ConfigureAwait(false);
            case SourceFormat.Xtream:
                detection.Definition = BuildXtream(uri!, input);
                detection.Summary = "Xtream Codes server: live channels, categories and EPG are imported.";
                break;
            case SourceFormat.M3u:
                var playlist = M3uParser.Parse(text);
                detection.Definition = BuildM3u(uri, input, playlist);
                detection.Summary = $"M3U playlist with {playlist.Entries.Count} entries" + (playlist.GuideUrl is null ? "." : " and an XMLTV guide.");
                break;
            case SourceFormat.DirectStream:
                detection.Definition = BuildDirect(uri!, input, StreamClassifier.FromUrl(uri!.ToString()) == StreamKind.Auto ? GuessKind(head, text, contentType) : StreamClassifier.FromUrl(uri.ToString()));
                detection.Summary = "Direct stream: it becomes a 24/7 channel.";
                break;
            case SourceFormat.WebPage:
                detection.Definition = BuildDirect(uri!, input, StreamKind.Embed);
                detection.Summary = "Web page: the resolver will look for the video stream inside it (24/7 channel).";
                detection.Warnings.Add("Pages need the resolver; use 'Test playback' to verify.");
                break;
            case SourceFormat.Xmltv:
                detection.Summary = "XMLTV guide: it only contains the EPG. Add it as 'xmltvUrl' of an M3U provider.";
                detection.Definition = BuildM3u(null, input, new M3uPlaylist());
                detection.Definition.M3u!.XmltvUrl = uri?.ToString();
                detection.Definition.M3u.Url = "https://example.com/playlist.m3u";
                break;
            case SourceFormat.RestJson:
                JsonNode? json;
                try
                {
                    json = BodyParser.ParseJson(text);
                }
                catch (ProviderRequestException ex)
                {
                    detection.Warnings.Add(ex.Message);
                    return detection;
                }

                var suggestion = MappingSuggester.Suggest(json, headers, input.Documentation);
                detection.Definition = BuildRest(uri, input, suggestion);
                detection.Fields.AddRange(suggestion.Report);
                detection.StreamFields.AddRange(suggestion.StreamReport);
                detection.Warnings.AddRange(suggestion.Warnings);
                detection.Summary = $"REST JSON API: {suggestion.ItemCount} {(suggestion.Role == "channels" ? "channels" : "events")} found at {suggestion.Root ?? "$"}"
                    + (suggestion.Pagination is null ? "." : $", pagination '{suggestion.Pagination.Type}'.");
                break;
            default:
                detection.Summary = "Format not recognised.";
                detection.Warnings.Add("The response is not JSON, M3U, XMLTV, Xtream, a stream or a web page.");
                break;
        }

        if (detection.Definition is not null)
        {
            detection.Definition = DefinitionStore.Masked(detection.Definition);
        }

        return detection;
    }

    /// <summary>Runs a definition in preview mode and reports what it extracts.</summary>
    /// <param name="definition">The definition (masked secrets are restored from the store).</param>
    /// <param name="sample">Optional pasted sample body.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The preview.</returns>
    public async Task<WizardPreview> PreviewAsync(ProviderDefinition definition, string? sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var preview = new WizardPreview();
        var restored = _store.WithPlainSecrets(_store.RestoreMaskedSecrets(DefinitionSerializer.Clone(definition)));
        var validation = DefinitionValidator.Validate(restored);
        preview.ValidationErrors.AddRange(validation.Errors);
        preview.ValidationWarnings.AddRange(validation.Warnings);
        preview.Valid = validation.IsValid;
        if (!validation.IsValid)
        {
            return preview;
        }

        var gateway = new ProviderGateway(restored.Id + "#preview", _http, _engine.Clock) { BypassBreaker = true };
        var result = await _engine.RunAsync(restored, new RunOptions { Preview = true, SampleBody = string.IsNullOrWhiteSpace(sample) ? null : sample, MaxItems = 1000 }, cancellationToken, gateway).ConfigureAwait(false);
        preview.Requests.AddRange(result.Diagnostics.Requests);
        preview.Errors.AddRange(result.Diagnostics.Errors);
        preview.Warnings.AddRange(result.Diagnostics.Warnings);
        foreach (var s in result.Diagnostics.Samples)
        {
            preview.Samples[s.Key] = new JsonArray(s.Value.Select(n => n?.DeepClone()).ToArray()).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        var snapshot = result.Snapshot;
        var now = _engine.Clock.GetUtcNow();

        // Lazy stream chains: try the first events so the admin sees the chain works.
        if (restored.Streams is not null && snapshot.Events.Count > 0)
        {
            foreach (var ev in snapshot.Events.Where(e => e.HasLazyStreams).Take(2))
            {
                var ctx = _engine.CreateContext(restored, new RunOptions { Preview = true }, gateway);
                var streams = await StreamChainRunner.FetchAsync(ctx, ev, cancellationToken).ConfigureAwait(false);
                ev.Streams.AddRange(streams.Where(s => !ev.Streams.Exists(x => x.Id == s.Id)));
                preview.Requests.AddRange(ctx.Diagnostics.Requests);
                preview.Errors.AddRange(ctx.Diagnostics.Errors);
            }
        }

        foreach (var ev in snapshot.Events.Take(100))
        {
            var row = new PreviewEvent
            {
                Id = ev.ExternalId,
                Key = ev.Key,
                Title = ev.Title,
                Category = ev.Category,
                Competition = ev.Competition,
                Start = ev.Start,
                Status = (ev.StatusFlag ?? (ev.IsLiveFlag == true ? EventStatus.Live : null))?.ToString(),
                Language = ev.Language,
                Country = ev.Country,
                Image = ev.ThumbUrl ?? ev.PosterUrl ?? ev.LogoUrl,
                StreamCount = ev.Streams.Count,
                LazyStreams = ev.HasLazyStreams,
            };
            AddStreams(row, ev.Streams, restored);
            if (ev.Streams.Count == 0 && !ev.HasLazyStreams)
            {
                row.Problems.Add("streams");
            }

            if (ev.Start < now.AddDays(-2) || ev.Start > now.AddDays(60))
            {
                row.Problems.Add("start");
            }

            if (ev.Language is null)
            {
                row.Problems.Add("language");
            }

            if (row.Image is null)
            {
                row.Problems.Add("image");
            }

            preview.Rows.Add(row);
        }

        foreach (var ch in snapshot.Channels.Take(Math.Max(0, 100 - preview.Rows.Count)))
        {
            var row = new PreviewEvent
            {
                Id = ch.ExternalId,
                Key = ch.Key,
                Title = ch.Name,
                Category = ch.Category,
                Language = ch.Language,
                Country = ch.Country,
                Image = ch.LogoUrl,
                StreamCount = ch.Streams.Count,
                IsChannel = true,
            };
            AddStreams(row, ch.Streams, restored);
            if (ch.Streams.Count == 0)
            {
                row.Problems.Add("streams");
            }

            if (ch.Language is null)
            {
                row.Problems.Add("language");
            }

            preview.Rows.Add(row);
        }

        preview.Counts["events"] = snapshot.Events.Count;
        preview.Counts["live"] = snapshot.Events.Count(e => e.IsLiveFlag == true || e.StatusFlag == EventStatus.Live);
        preview.Counts["channels"] = snapshot.Channels.Count;
        preview.Counts["programs"] = snapshot.Programs.Count;
        preview.Counts["categories"] = snapshot.Categories.Count;
        preview.Counts["streams"] = snapshot.Events.Sum(e => e.Streams.Count) + snapshot.Channels.Sum(c => c.Streams.Count);
        preview.Counts["withoutStreams"] = snapshot.Events.Count(e => e.Streams.Count == 0 && !e.HasLazyStreams) + snapshot.Channels.Count(c => c.Streams.Count == 0);
        preview.Counts["withoutImage"] = snapshot.Events.Count(e => e.ThumbUrl is null && e.PosterUrl is null && e.LogoUrl is null);
        preview.Counts["languages"] = snapshot.Events.Select(e => e.Language).Concat(snapshot.Channels.Select(c => c.Language)).Where(l => l is not null).Distinct().Count();
        return preview;
    }

    /// <summary>Recognises a format from a probe.</summary>
    /// <param name="uri">The URL.</param>
    /// <param name="head">First bytes.</param>
    /// <param name="text">Body text.</param>
    /// <param name="contentType">Content type.</param>
    /// <returns>The format.</returns>
    public static SourceFormat DetectFormat(Uri? uri, byte[] head, string text, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(text);
        var ct = contentType ?? string.Empty;
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (uri is not null && uri.AbsolutePath.EndsWith("player_api.php", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Xtream;
        }

        if (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
        {
            return M3uParser.IsHlsPlaylist(trimmed) ? SourceFormat.DirectStream : SourceFormat.M3u;
        }

        if (ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || ct.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || ct.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
            || (head.Length > 188 && head[0] == 0x47 && head[188] == 0x47)
            || (head.Length > 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p'))
        {
            return SourceFormat.DirectStream;
        }

        if (trimmed.StartsWith('<'))
        {
            if (trimmed.Contains("<MPD", StringComparison.Ordinal))
            {
                return SourceFormat.DirectStream;
            }

            if (trimmed.Contains("<tv", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<programme", StringComparison.OrdinalIgnoreCase))
            {
                return SourceFormat.Xmltv;
            }

            if (trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
            {
                return SourceFormat.WebPage;
            }
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || ct.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Contains("\"openapi\"", StringComparison.Ordinal) || trimmed.Contains("\"swagger\"", StringComparison.Ordinal))
            {
                return SourceFormat.OpenApi;
            }

            if (trimmed.Contains("\"user_info\"", StringComparison.Ordinal) && trimmed.Contains("\"server_info\"", StringComparison.Ordinal))
            {
                return SourceFormat.Xtream;
            }

            return SourceFormat.RestJson;
        }

        if (trimmed.StartsWith("openapi:", StringComparison.Ordinal) || trimmed.StartsWith("swagger:", StringComparison.Ordinal))
        {
            return SourceFormat.OpenApi;
        }

        return SourceFormat.Unknown;
    }

    private async Task<(byte[] Head, string Text, string? ContentType, IReadOnlyDictionary<string, string> Headers, int Status)> ProbeAsync(Uri uri, WizardInput input, CancellationToken cancellationToken)
    {
        var def = ProbeDefinition(uri, input);
        var ctx = _engine.CreateContext(def, new RunOptions { Preview = true }, new ProviderGateway("wizard-probe", _http, _engine.Clock) { BypassBreaker = true });
        var request = ctx.NewRequest(uri);
        request.MaxBytes = ProbeBytes;
        foreach (var h in input.Headers ?? new Dictionary<string, string>())
        {
            request.Headers[h.Key] = h.Value;
        }

        await AuthHandler.ApplyAsync(request, ctx, cancellationToken).ConfigureAwait(false);
        var response = await _http.OpenStreamAsync(request, cancellationToken).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            var buffer = new byte[ProbeBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await response.Stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            var bytes = buffer.AsSpan(0, total).ToArray();
            var decoded = BodyParser.MaybeGunzip(bytes, ProbeBytes * 8);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (response.Header("Link") is { } link)
            {
                headers["Link"] = link;
            }

            return (bytes.Length > 4096 ? bytes[..4096] : bytes, BodyParser.Decode(decoded), response.ContentType, headers, response.StatusCode);
        }
    }

    private static ProviderDefinition ProbeDefinition(Uri uri, WizardInput input) => new()
    {
        Id = "wizard-probe",
        Name = "Wizard",
        Kind = ProviderKind.Direct,
        AllowPrivateNetwork = input.AllowPrivateNetwork,
        EgressProfile = input.EgressProfile,
        Secrets = input.Secrets is null ? null : new Dictionary<string, string>(input.Secrets),
        Connection = new ConnectionSpec { BaseUrl = uri.GetLeftPart(UriPartial.Authority), Auth = input.Auth, Headers = input.Headers, TimeoutSeconds = 20 },
        Direct = new DirectSpec { Url = uri.ToString() },
    };

    private ProviderDefinition Base(Uri? uri, WizardInput input, ProviderKind kind, string? name = null)
    {
        var host = uri?.Host ?? "my-api";
        var id = _store.UniqueId(host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase).Split('.').FirstOrDefault() ?? "provider");
        return new ProviderDefinition
        {
            SchemaVersion = 1,
            Id = id,
            Name = name ?? host,
            Kind = kind,
            Priority = 100,
            AllowPrivateNetwork = input.AllowPrivateNetwork,
            EgressProfile = string.IsNullOrWhiteSpace(input.EgressProfile) ? null : input.EgressProfile,
            Secrets = input.Secrets is { Count: > 0 } ? new Dictionary<string, string>(input.Secrets) : null,
            Defaults = new DefaultsSpec { Timezone = "UTC" },
            Resolver = new ResolverSpec { Enabled = true, Strategy = ResolverStrategy.Auto },
        };
    }

    private ProviderDefinition BuildRest(Uri? uri, WizardInput input, MappingSuggestion s)
    {
        var def = Base(uri, input, ProviderKind.Rest);
        var baseUrl = uri?.GetLeftPart(UriPartial.Authority) ?? "https://example.invalid";
        var path = uri is null ? "/" : uri.PathAndQuery;

        // Pagination parameters already in the URL are moved to the pagination settings (it adds them on every page).
        if (s.Pagination is { } pg && pg.Type != PaginationType.None && uri is not null)
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            var changed = false;
            if (pg.Param is { } param && query[param] is not null)
            {
                query.Remove(param);
                changed = true;
            }

            var sizeKey = pg.SizeParam ?? new[] { "limit", "per_page", "perPage", "pageSize", "page_size", "size", "count" }.FirstOrDefault(k => query[k] is not null);
            if (sizeKey is not null && query[sizeKey] is { } sizeValue)
            {
                pg.SizeParam = sizeKey;
                if (int.TryParse(sizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0)
                {
                    pg.Size ??= size;
                }

                query.Remove(sizeKey);
                changed = true;
            }

            if (changed)
            {
                var q = query.ToString();
                path = uri.AbsolutePath + (string.IsNullOrEmpty(q) ? string.Empty : "?" + q);
            }
        }

        def.Connection = new ConnectionSpec
        {
            BaseUrl = baseUrl,
            Headers = input.Headers is { Count: > 0 } ? new Dictionary<string, string>(input.Headers) : new Dictionary<string, string> { ["Accept"] = "application/json" },
            Auth = input.Auth,
            TimeoutSeconds = 15,
        };

        var fields = new FieldMap();
        foreach (var kv in s.Fields)
        {
            fields[kv.Key] = kv.Value;
        }

        fields.Participants = s.Participants;
        var endpoint = new EndpointSpec
        {
            Id = s.Role == "channels" ? "channels" : "events",
            Scope = EndpointScope.All,
            Request = new RequestSpec { Url = "{baseUrl}" + path },
            Root = s.Root,
            Fields = fields,
            Streams = s.Streams,
            Pagination = s.Pagination,
        };

        if (s.Role == "channels")
        {
            def.Channels = new List<EndpointSpec> { endpoint };
        }
        else
        {
            def.Events = new List<EndpointSpec> { endpoint };
        }

        return def;
    }

    private ProviderDefinition BuildM3u(Uri? uri, WizardInput input, M3uPlaylist playlist)
    {
        var def = Base(uri, input, ProviderKind.M3u);
        def.Defaults = new DefaultsSpec { Category = "TV" };
        def.M3u = new M3uSpec
        {
            Url = uri?.ToString() ?? string.Empty,
            XmltvUrl = playlist.GuideUrl,
            Mode = playlist.Entries.Count > 0 && playlist.Entries.Take(30).Count(e => System.Text.RegularExpressions.Regex.IsMatch(e.Name, @"\b\d{1,2}[:.]\d{2}\b", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50))) > Math.Min(15, playlist.Entries.Count / 2)
                ? M3uMode.Events
                : M3uMode.Channels,
            ExcludeGroups = "(?i)adult|xxx",
        };
        return def;
    }

    private ProviderDefinition BuildXtream(Uri uri, WizardInput input)
    {
        var def = Base(uri, input, ProviderKind.Xtream, uri.Host + " (Xtream)");
        var query = HttpUtility.ParseQueryString(uri.Query);
        var user = query["username"] ?? input.Secrets?.GetValueOrDefault("username") ?? string.Empty;
        var pass = query["password"] ?? input.Secrets?.GetValueOrDefault("password") ?? string.Empty;
        def.Secrets = new Dictionary<string, string> { ["username"] = user, ["password"] = pass };
        var path = uri.AbsolutePath;
        var prefix = path.EndsWith("player_api.php", StringComparison.OrdinalIgnoreCase) ? path[..^"player_api.php".Length].TrimEnd('/') : string.Empty;
        def.Xtream = new XtreamSpec
        {
            Server = uri.GetLeftPart(UriPartial.Authority) + prefix,
            Username = "{secret.username}",
            Password = "{secret.password}",
            Output = XtreamOutput.M3u8,
            IncludeEpg = true,
            ExcludeCategories = "(?i)adult|xxx",
        };
        return def;
    }

    private ProviderDefinition BuildDirect(Uri uri, WizardInput input, StreamKind kind)
    {
        var def = Base(uri, input, ProviderKind.Direct);
        def.Defaults = new DefaultsSpec { Category = "TV" };
        def.Direct = new DirectSpec
        {
            Url = uri.ToString(),
            Name = uri.Host,
            Type = (StreamTypeSpec)(int)kind,
            Headers = input.Headers is { Count: > 0 } ? new Dictionary<string, string>(input.Headers) : null,
        };
        return def;
    }

    private async Task<WizardDetection> FromOpenApiAsync(OpenApiDocument spec, WizardInput input, WizardDetection detection, CancellationToken cancellationToken)
    {
        detection.Format = SourceFormat.OpenApi;
        detection.Operations.AddRange(spec.Operations);
        detection.Summary = $"OpenAPI '{spec.Title}': {spec.Operations.Count} operations. Choose the one that lists events.";
        var chosen = spec.Operations.FirstOrDefault(o => string.Equals(o.Key, input.OpenApiOperation, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return detection;
        }

        var baseUrl = spec.BaseUrl ?? input.Url;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(input.Url, UriKind.Absolute, out var inputUri))
        {
            Uri.TryCreate(inputUri, baseUrl, out baseUri);
        }

        if (baseUri is null)
        {
            detection.Warnings.Add("The specification has no server URL: enter the API base URL.");
            return detection;
        }

        var sampleUrl = new Uri(baseUri.ToString().TrimEnd('/') + chosen.Path);
        var probeInput = new WizardInput
        {
            Url = sampleUrl.ToString(),
            Auth = input.Auth ?? spec.Auth,
            Secrets = input.Secrets,
            Headers = input.Headers,
            AllowPrivateNetwork = input.AllowPrivateNetwork,
            EgressProfile = input.EgressProfile,
            Documentation = input.Documentation,
            SampleResponse = input.SampleResponse,
        };
        var inner = await DetectAsync(probeInput, cancellationToken).ConfigureAwait(false);
        detection.Fields.AddRange(inner.Fields);
        detection.StreamFields.AddRange(inner.StreamFields);
        detection.Warnings.AddRange(inner.Warnings);
        detection.Sample = inner.Sample;
        detection.HttpStatus = inner.HttpStatus;
        if (inner.Definition is { } def)
        {
            def.Connection ??= new ConnectionSpec();
            def.Connection.BaseUrl = baseUri.ToString().TrimEnd('/');
            def.Connection.Auth ??= spec.Auth;
            def.Name = spec.Title ?? def.Name;
            def.OpenApi = new OpenApiSpec { SelectedOperations = new List<string> { chosen.Key } };
            var endpoint = def.Events?.FirstOrDefault() ?? def.Channels?.FirstOrDefault();
            if (endpoint is not null)
            {
                endpoint.Request.Url = OpenApiParser.ToUrlTemplate(chosen);
            }

            // A categories operation without parameters becomes the categories endpoint.
            var categoriesOp = spec.Operations.FirstOrDefault(o => o.SuggestedRole == "categories");
            if (categoriesOp is not null)
            {
                def.Categories = new EndpointSpec { Request = new RequestSpec { Url = OpenApiParser.ToUrlTemplate(categoriesOp) }, Fields = new FieldMap { ["id"] = FieldSpec.OfPath("$.id"), ["name"] = FieldSpec.OfPath("$.name") } };
                detection.Warnings.Add($"Categories endpoint '{categoriesOp.Key}' added: verify its root and fields.");
            }

            detection.Definition = def;
        }

        detection.Format = SourceFormat.OpenApi;
        return detection;
    }

    private static StreamKind GuessKind(byte[] head, string text, string? contentType)
    {
        var ct = contentType ?? string.Empty;
        if (text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal) || ct.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            return StreamKind.Hls;
        }

        if (text.Contains("<MPD", StringComparison.Ordinal) || ct.Contains("dash", StringComparison.OrdinalIgnoreCase))
        {
            return StreamKind.Dash;
        }

        if (head.Length > 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
        {
            return StreamKind.Mp4;
        }

        return StreamKind.Ts;
    }

    private static void AddStreams(PreviewEvent row, IEnumerable<LiveStream> streams, ProviderDefinition def)
    {
        var secrets = (def.Secrets ?? new Dictionary<string, string>()).Values.ToList();
        foreach (var s in streams.Take(10))
        {
            row.Streams.Add(new PreviewStreamInfo
            {
                Id = s.Id,
                Url = Redactor.Redact(s.Url, secrets),
                Kind = s.Kind.ToString(),
                Quality = s.QualityLabel ?? StreamClassifier.LabelFromHeight(s.Height),
                Language = s.Language,
                Label = s.Label,
                Headers = s.Headers.Count,
            });
        }
    }
}
