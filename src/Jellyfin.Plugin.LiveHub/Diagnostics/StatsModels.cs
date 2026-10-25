using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.LiveHub.Diagnostics;

/// <summary>
/// Live counters of a provider, shown in the diagnostics page.
/// </summary>
public sealed class ProviderStats
{
    private readonly ConcurrentDictionary<string, EndpointStats> _endpoints = new(StringComparer.Ordinal);
    private long _requests;
    private long _notModified;
    private long _failures;

    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the last sync attempt.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Gets or sets the last successful sync.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Gets or sets the last error (redacted).</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the last error time.</summary>
    public DateTimeOffset? LastErrorAt { get; set; }

    /// <summary>Gets or sets the consecutive failures.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Gets or sets the number of events.</summary>
    public int Events { get; set; }

    /// <summary>Gets or sets the number of streams.</summary>
    public int Streams { get; set; }

    /// <summary>Gets or sets the number of channels.</summary>
    public int Channels { get; set; }

    /// <summary>Gets or sets the number of programmes.</summary>
    public int Programs { get; set; }

    /// <summary>Gets or sets the circuit state.</summary>
    public string Circuit { get; set; } = "Closed";

    /// <summary>Gets or sets the egress profile id in use.</summary>
    public string EgressProfile { get; set; } = "direct";

    /// <summary>Gets or sets a value indicating whether data comes from a disk snapshot (API unreachable).</summary>
    public bool FromSnapshot { get; set; }

    /// <summary>Gets the total requests.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>Gets the 304 responses.</summary>
    public long NotModified => Interlocked.Read(ref _notModified);

    /// <summary>Gets the failed requests.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Gets the per-endpoint statistics.</summary>
    public IReadOnlyList<EndpointStats> Endpoints => _endpoints.Values.OrderBy(e => e.Endpoint, StringComparer.Ordinal).ToList();

    /// <summary>Records a request.</summary>
    /// <param name="endpoint">The endpoint id.</param>
    /// <param name="ms">Latency.</param>
    /// <param name="ok">Success.</param>
    /// <param name="notModified">304.</param>
    public void Record(string endpoint, double ms, bool ok, bool notModified)
    {
        Interlocked.Increment(ref _requests);
        if (notModified)
        {
            Interlocked.Increment(ref _notModified);
        }

        if (!ok)
        {
            Interlocked.Increment(ref _failures);
        }

        var e = _endpoints.GetOrAdd(string.IsNullOrEmpty(endpoint) ? "-" : endpoint, k => new EndpointStats { Endpoint = k });
        e.Record(ms, ok);
    }
}

/// <summary>Per-endpoint statistics.</summary>
public sealed class EndpointStats
{
    private readonly Lock _lock = new();

    /// <summary>Gets or sets the endpoint id.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets the request count.</summary>
    public long Count { get; private set; }

    /// <summary>Gets the error count.</summary>
    public long Errors { get; private set; }

    /// <summary>Gets the average latency (EWMA).</summary>
    public double AvgMs { get; private set; }

    /// <summary>Gets the last latency.</summary>
    public double LastMs { get; private set; }

    /// <summary>Gets the last request time.</summary>
    public DateTimeOffset? LastAt { get; private set; }

    /// <summary>Records a request.</summary>
    /// <param name="ms">Latency.</param>
    /// <param name="ok">Success.</param>
    public void Record(double ms, bool ok)
    {
        lock (_lock)
        {
            Count++;
            if (!ok)
            {
                Errors++;
            }

            LastMs = ms;
            AvgMs = Count == 1 ? ms : (AvgMs * 0.8) + (ms * 0.2);
            LastAt = DateTimeOffset.UtcNow;
        }
    }
}
