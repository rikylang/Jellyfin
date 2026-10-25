using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Engine;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>
/// Short-lived in-memory cache of relayed segments: viewers of the same stream (and the ffmpeg probe) share one
/// origin request per segment. Bounded by size and age.
/// </summary>
public sealed class SegmentCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, CachedSegment Segment)> _entries = new(StringComparer.Ordinal);
    private readonly SingleFlight<CachedSegment> _flight = new();
    private readonly TimeProvider _clock;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private long _bytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentCache"/> class.
    /// </summary>
    /// <param name="clock">Clock.</param>
    /// <param name="maxBytes">Maximum total size.</param>
    /// <param name="maxAge">Maximum age.</param>
    public SegmentCache(TimeProvider? clock = null, long maxBytes = 128L * 1024 * 1024, TimeSpan? maxAge = null)
    {
        _clock = clock ?? TimeProvider.System;
        _maxBytes = maxBytes;
        _maxAge = maxAge ?? TimeSpan.FromSeconds(90);
    }

    /// <summary>Gets the cached bytes.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Gets a segment from the cache or fetches it once for every concurrent caller.</summary>
    /// <param name="key">The cache key (origin URL).</param>
    /// <param name="fetch">The fetch operation (receives its own cancellation token).</param>
    /// <param name="cancellationToken">Cancellation of this caller.</param>
    /// <returns>The segment.</returns>
    public async Task<CachedSegment> GetAsync(string key, Func<CancellationToken, Task<CachedSegment>> fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var now = _clock.GetUtcNow();
        if (_entries.TryGetValue(key, out var hit) && now - hit.At < _maxAge)
        {
            return hit.Segment;
        }

        var task = _flight.RunAsync(key, async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var segment = await fetch(cts.Token).ConfigureAwait(false);
            if (segment.Status is >= 200 and < 300)
            {
                Add(key, segment);
            }

            return segment;
        });
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops expired entries.</summary>
    public void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var kv in _entries.Where(kv => now - kv.Value.At >= _maxAge).ToList())
        {
            if (_entries.TryRemove(kv.Key, out var removed))
            {
                Interlocked.Add(ref _bytes, -removed.Segment.Data.Length);
            }
        }
    }

    private void Add(string key, CachedSegment segment)
    {
        var now = _clock.GetUtcNow();
        if (_entries.TryAdd(key, (now, segment)))
        {
            Interlocked.Add(ref _bytes, segment.Data.Length);
        }

        if (Bytes <= _maxBytes && _entries.Count < 4096)
        {
            return;
        }

        foreach (var kv in _entries.OrderBy(kv => kv.Value.At).ToList())
        {
            if (Bytes <= _maxBytes * 3 / 4 && now - kv.Value.At < _maxAge && _entries.Count < 3072)
            {
                break;
            }

            if (_entries.TryRemove(kv.Key, out var removed))
            {
                Interlocked.Add(ref _bytes, -removed.Segment.Data.Length);
            }
        }
    }
}
