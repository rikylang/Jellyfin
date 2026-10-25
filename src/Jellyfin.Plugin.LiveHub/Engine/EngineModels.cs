using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>Options of a provider run.</summary>
public sealed class RunOptions
{
    /// <summary>Gets or sets the endpoint ids to run (null = all).</summary>
    public IReadOnlySet<string>? EndpointIds { get; set; }

    /// <summary>Gets or sets a value indicating whether this is a wizard preview (page/item limits, raw samples).</summary>
    public bool Preview { get; set; }

    /// <summary>Gets or sets the maximum pages per endpoint in preview mode.</summary>
    public int PreviewMaxPages { get; set; } = 3;

    /// <summary>Gets or sets the maximum items per endpoint.</summary>
    public int MaxItems { get; set; } = 5000;

    /// <summary>Gets or sets a value indicating whether non-lazy stream chains run during the sync.</summary>
    public bool RunEagerStreamChains { get; set; } = true;

    /// <summary>Gets or sets a sample body used instead of fetching the first request (wizard "sample response").</summary>
    public string? SampleBody { get; set; }
}

/// <summary>One HTTP request performed during a run.</summary>
public sealed class RequestLog
{
    /// <summary>Gets or sets the endpoint id.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the redacted URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTP status (0 = network error).</summary>
    public int Status { get; set; }

    /// <summary>Gets or sets the latency.</summary>
    public double Ms { get; set; }

    /// <summary>Gets or sets the body size.</summary>
    public long Bytes { get; set; }

    /// <summary>Gets or sets the number of items selected by the root path.</summary>
    public int Items { get; set; }

    /// <summary>Gets or sets a value indicating whether the body came from the conditional cache (304).</summary>
    public bool NotModified { get; set; }

    /// <summary>Gets or sets the error.</summary>
    public string? Error { get; set; }
}

/// <summary>Diagnostics collected during a run.</summary>
public sealed class RunDiagnostics
{
    /// <summary>Gets the requests.</summary>
    public List<RequestLog> Requests { get; } = new();

    /// <summary>Gets the warnings (non fatal).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Gets the errors.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Gets raw sample items per endpoint (preview only).</summary>
    public Dictionary<string, List<JsonNode?>> Samples { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds a warning (bounded).</summary>
    /// <param name="message">The message.</param>
    public void Warn(string message)
    {
        if (Warnings.Count < 200 && !Warnings.Contains(message))
        {
            Warnings.Add(message);
        }
    }
}

/// <summary>Result of one endpoint.</summary>
public sealed class EndpointResult
{
    /// <summary>Gets or sets the endpoint id.</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>Gets or sets the role (categories, events, channels).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the endpoint succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the error.</summary>
    public string? Error { get; set; }

    /// <summary>Gets the events.</summary>
    public List<LiveEvent> Events { get; } = new();

    /// <summary>Gets the channels.</summary>
    public List<LiveChannel> Channels { get; } = new();

    /// <summary>Gets the categories.</summary>
    public List<LiveCategory> Categories { get; } = new();

    /// <summary>Gets the programmes.</summary>
    public List<LiveProgram> Programs { get; } = new();

    /// <summary>Gets or sets the server-suggested retry delay (429/503 Retry-After).</summary>
    public TimeSpan? RetryAfter { get; set; }
}

/// <summary>Result of a provider run.</summary>
public sealed class ProviderRunResult
{
    /// <summary>Gets or sets the snapshot.</summary>
    public ProviderSnapshot Snapshot { get; set; } = new();

    /// <summary>Gets the per-endpoint results.</summary>
    public List<EndpointResult> Endpoints { get; } = new();

    /// <summary>Gets the diagnostics.</summary>
    public RunDiagnostics Diagnostics { get; } = new();

    /// <summary>Gets a value indicating whether every endpoint succeeded.</summary>
    public bool Success => Endpoints.TrueForAll(e => e.Success) && Diagnostics.Errors.Count == 0;
}

/// <summary>Thrown when an endpoint returns an unusable response.</summary>
public sealed class ProviderRequestException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRequestException"/> class.
    /// </summary>
    public ProviderRequestException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRequestException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ProviderRequestException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRequestException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public ProviderRequestException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Gets or sets the HTTP status.</summary>
    public int StatusCode { get; set; }

    /// <summary>Gets or sets the Retry-After delay.</summary>
    public TimeSpan? RetryAfter { get; set; }
}

/// <summary>Thrown when the provider circuit breaker is open.</summary>
public sealed class CircuitOpenException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitOpenException"/> class.
    /// </summary>
    public CircuitOpenException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitOpenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public CircuitOpenException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitOpenException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public CircuitOpenException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
