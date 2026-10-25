using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>
/// One origin connection for a continuous MPEG-TS stream, broadcast to every subscriber (shared live stream).
/// Slow subscribers lose their oldest chunks instead of slowing the others. On origin errors the opener is called
/// again (it performs re-resolution/failover); the pump stops when nobody listens.
/// </summary>
public sealed class TsFanout : IAsyncDisposable
{
    private const int ChunkSize = 64 * 1024;

    private readonly Func<int, CancellationToken, Task<Stream>> _open;
    private readonly ILogger _logger;
    private readonly List<Channel<ReadOnlyMemory<byte>>> _subscribers = new();
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private long _bytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="TsFanout"/> class.
    /// </summary>
    /// <param name="open">Opens the origin; receives the attempt number (0 = first connection).</param>
    /// <param name="logger">Logger.</param>
    public TsFanout(Func<int, CancellationToken, Task<Stream>> open, ILogger logger)
    {
        _open = open;
        _logger = logger;
    }

    /// <summary>Gets the bytes received from the origin.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Gets the number of subscribers.</summary>
    public int Subscribers
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Gets a value indicating whether the pump has stopped for good.</summary>
    public bool Completed => _pump is { IsCompleted: true };

    /// <summary>Subscribes to the stream (starts the origin connection on the first subscriber).</summary>
    /// <returns>The reader.</returns>
    public ChannelReader<ReadOnlyMemory<byte>> Subscribe()
    {
        var channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
        lock (_lock)
        {
            _subscribers.Add(channel);
            if (_pump is null || _pump.IsCompleted)
            {
                _pump = Task.Run(() => PumpAsync(_stop.Token));
            }
        }

        return channel.Reader;
    }

    /// <summary>Removes a subscriber.</summary>
    /// <param name="reader">The reader returned by <see cref="Subscribe"/>.</param>
    public void Unsubscribe(ChannelReader<ReadOnlyMemory<byte>> reader)
    {
        lock (_lock)
        {
            _subscribers.RemoveAll(c => c.Reader == reader);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var buffer = new byte[ChunkSize];
        var idleSince = DateTime.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (Subscribers == 0)
            {
                if (DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(10))
                {
                    break;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Stream origin;
            try
            {
                origin = await _open(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("TS relay: origin unavailable ({Error})", ex.Message);
                break;
            }

            var noListeners = false;
            await using (origin.ConfigureAwait(false))
            {
                try
                {
                    int read;
                    while (!noListeners && (read = await origin.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        attempt = 0;
                        Interlocked.Add(ref _bytes, read);
                        var chunk = buffer.AsSpan(0, read).ToArray();
                        lock (_lock)
                        {
                            foreach (var s in _subscribers)
                            {
                                s.Writer.TryWrite(chunk);
                            }

                            noListeners = _subscribers.Count == 0;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException)
                {
                    _logger.LogInformation("TS relay: origin connection lost ({Error}), reconnecting", ex.Message);
                }
            }

            if (noListeners)
            {
                idleSince = DateTime.UtcNow;
                continue;
            }

            attempt++;
            if (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(Math.Min(attempt, 5) * 300, cancellationToken).ConfigureAwait(false);
            }
        }

        lock (_lock)
        {
            foreach (var s in _subscribers)
            {
                s.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}
