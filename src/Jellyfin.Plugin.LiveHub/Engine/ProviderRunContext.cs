using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Everything a run needs: the definition (with plain-text secrets, in memory only), the gateway, templates, limits.
/// </summary>
public sealed class ProviderRunContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRunContext"/> class.
    /// </summary>
    /// <param name="definition">The definition with decrypted secrets.</param>
    /// <param name="gateway">The gateway.</param>
    /// <param name="egressProfileId">The resolved egress profile.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Run options.</param>
    public ProviderRunContext(ProviderDefinition definition, ProviderGateway gateway, string egressProfileId, ILogger logger, RunOptions? options = null)
    {
        Definition = definition;
        Gateway = gateway;
        EgressProfileId = egressProfileId;
        Logger = logger;
        Options = options ?? new RunOptions();
        Clock = gateway.Clock;
        TimeZone = DateParser.FindZone(definition.Defaults?.Timezone) ?? TimeZoneInfo.Utc;
        Template = new TemplateContext(Clock, TimeZone);

        foreach (var kv in definition.Variables ?? new Dictionary<string, string>())
        {
            Template["var." + kv.Key] = kv.Value;
        }

        foreach (var kv in definition.Secrets ?? new Dictionary<string, string>())
        {
            Template["secret." + kv.Key] = kv.Value;
        }

        BaseUrl = Template.Render(definition.Connection?.BaseUrl).TrimEnd('/');
        Template["baseUrl"] = BaseUrl;
        Template["provider.id"] = definition.Id;
        Template["provider.name"] = definition.Name;
        SecretValues = (definition.Secrets ?? new Dictionary<string, string>()).Values.Where(v => !string.IsNullOrEmpty(v)).ToList();
    }

    /// <summary>Gets the definition.</summary>
    public ProviderDefinition Definition { get; }

    /// <summary>Gets the gateway.</summary>
    public ProviderGateway Gateway { get; }

    /// <summary>Gets the egress profile id.</summary>
    public string EgressProfileId { get; }

    /// <summary>Gets the logger.</summary>
    public ILogger Logger { get; }

    /// <summary>Gets the options.</summary>
    public RunOptions Options { get; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Gets the provider time zone (dates without offset).</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>Gets the root template context.</summary>
    public TemplateContext Template { get; }

    /// <summary>Gets the rendered base URL.</summary>
    public string BaseUrl { get; }

    /// <summary>Gets the secret values (for redaction).</summary>
    public IReadOnlyList<string> SecretValues { get; }

    /// <summary>Gets the diagnostics.</summary>
    public RunDiagnostics Diagnostics { get; } = new();

    /// <summary>Gets the provider id.</summary>
    public string ProviderId => Definition.Id;

    /// <summary>Gets the provider priority.</summary>
    public int Priority => Definition.Priority;

    /// <summary>Gets the request timeout.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(Definition.Connection?.TimeoutSeconds ?? 15, 2, 120));

    /// <summary>Gets the response size limit.</summary>
    public long MaxBytes => Math.Clamp(Definition.Connection?.MaxResponseBytes ?? (16L * 1024 * 1024), 1024, 64L * 1024 * 1024);

    /// <summary>Builds a base request (egress, limits, headers, user agent).</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The request.</returns>
    public FetchRequest NewRequest(Uri url)
    {
        var request = new FetchRequest
        {
            ProviderId = ProviderId,
            Url = url,
            Timeout = Timeout,
            MaxBytes = MaxBytes,
            EgressProfileId = EgressProfileId,
            AllowPrivateNetwork = Definition.AllowPrivateNetwork,
        };

        if (Definition.Connection?.Headers is { } headers)
        {
            foreach (var kv in headers)
            {
                request.Headers[kv.Key] = Template.Render(kv.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(Definition.Connection?.UserAgent))
        {
            request.Headers["User-Agent"] = Template.Render(Definition.Connection!.UserAgent);
        }

        return request;
    }

    /// <summary>Redacts a text with the provider secrets.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The redacted text.</returns>
    public string Redact(string? text) => Security.Redactor.Redact(text, SecretValues);
}
