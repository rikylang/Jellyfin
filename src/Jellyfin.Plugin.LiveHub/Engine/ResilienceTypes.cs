using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>Token bucket rate limiter (per provider).</summary>
public sealed class TokenBucket
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private double _tokens;
    private long _lastTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenBucket"/> class.
    /// </summary>
    /// <param name="requestsPerMinute">Sustained rate.</param>
    /// <param name="burst">Bucket size.</param>
    /// <param name="clock">Clock.</param>
    public TokenBucket(int requestsPerMinute, int burst, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        Configure(requestsPerMinute, burst);
        _tokens = Burst;
        _lastTicks = _clock.GetTimestamp();
    }

    /// <summary>Gets the sustained rate.</summary>
    public int RequestsPerMinute { get; private set; }

    /// <summary>Gets the burst.</summary>
    public int Burst { get; private set; }

    /// <summary>Updates the limits.</summary>
    /// <param name="requestsPerMinute">Sustained rate.</param>
    /// <param name="burst">Bucket size.</param>
    public void Configure(int requestsPerMinute, int burst)
    {
        RequestsPerMinute = Math.Clamp(requestsPerMinute, 1, 60_000);
        Burst = Math.Clamp(burst, 1, 1000);
    }

    /// <summary>Takes a token, waiting if necessary.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                Refill();
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }

                var perSecond = RequestsPerMinute / 60.0;
                wait = TimeSpan.FromSeconds((1 - _tokens) / perSecond);
            }

            await Task.Delay(wait < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : wait, _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tries to take a token without waiting.</summary>
    /// <returns>Whether a token was available.</returns>
    public bool TryTake()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return true;
            }

            return false;
        }
    }

    private void Refill()
    {
        var now = _clock.GetTimestamp();
        var elapsed = _clock.GetElapsedTime(_lastTicks, now).TotalSeconds;
        _lastTicks = now;
        _tokens = Math.Min(Burst, _tokens + (elapsed * RequestsPerMinute / 60.0));
    }
}

/// <summary>Circuit breaker state.</summary>
public enum CircuitState
{
    /// <summary>Requests flow.</summary>
    Closed,

    /// <summary>Requests are rejected until the cool-down ends.</summary>
    Open,

    /// <summary>One trial request is allowed.</summary>
    HalfOpen,
}

/// <summary>
/// Per-provider circuit breaker: opens after N consecutive failures, cool-down grows exponentially (max 15 min).
/// </summary>
public sealed class CircuitBreaker
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private readonly int _threshold;
    private int _failures;
    private int _opens;
    private DateTimeOffset _openUntil;
    private bool _trialInFlight;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreaker"/> class.
    /// </summary>
    /// <param name="threshold">Consecutive failures before opening.</param>
    /// <param name="clock">Clock.</param>
    public CircuitBreaker(int threshold = 5, TimeProvider? clock = null)
    {
        _threshold = threshold;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets the state.</summary>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                if (_failures < _threshold)
                {
                    return CircuitState.Closed;
                }

                return _clock.GetUtcNow() < _openUntil ? CircuitState.Open : CircuitState.HalfOpen;
            }
        }
    }

    /// <summary>Gets when the circuit closes again (if open).</summary>
    public DateTimeOffset OpenUntil => _openUntil;

    /// <summary>Checks whether a request may proceed.</summary>
    /// <returns>Whether allowed.</returns>
    public bool TryAcquire()
    {
        lock (_lock)
        {
            if (_failures < _threshold)
            {
                return true;
            }

            if (_clock.GetUtcNow() < _openUntil || _trialInFlight)
            {
                return false;
            }

            _trialInFlight = true;
            return true;
        }
    }

    /// <summary>Records a success.</summary>
    public void Success()
    {
        lock (_lock)
        {
            _failures = 0;
            _opens = 0;
            _trialInFlight = false;
        }
    }

    /// <summary>Records a failure.</summary>
    public void Failure()
    {
        lock (_lock)
        {
            _failures++;
            _trialInFlight = false;
            if (_failures >= _threshold)
            {
                _opens++;
                var seconds = Math.Min(900, 30 * Math.Pow(2, Math.Min(_opens - 1, 5)));
                _openUntil = _clock.GetUtcNow().AddSeconds(seconds);
            }
        }
    }
}

/// <summary>Deduplicates concurrent identical operations (single-flight).</summary>
/// <typeparam name="T">The result type.</typeparam>
public sealed class SingleFlight<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Gets the number of operations in flight.</summary>
    public int Count => _inFlight.Count;

    /// <summary>Runs the operation or joins an identical one already running.</summary>
    /// <param name="key">The operation key.</param>
    /// <param name="operation">The operation.</param>
    /// <returns>The shared result.</returns>
    public async Task<T> RunAsync(string key, Func<Task<T>> operation)
    {
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<T>>(operation, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new System.Collections.Generic.KeyValuePair<string, Lazy<Task<T>>>(key, lazy));
        }
    }
}

/// <summary>Exponential backoff with full jitter.</summary>
public static class Backoff
{
    /// <summary>Computes the delay for an attempt.</summary>
    /// <param name="attempt">Attempt number (1-based).</param>
    /// <param name="baseDelay">Base delay.</param>
    /// <param name="max">Maximum delay.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan Delay(int attempt, TimeSpan baseDelay, TimeSpan max)
    {
        var exp = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 16));
        var capped = Math.Min(exp, max.TotalMilliseconds);
        var jittered = (capped / 2) + (Random.Shared.NextDouble() * capped / 2);
        return TimeSpan.FromMilliseconds(jittered);
    }
}
