using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Relay;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>
/// A Jellyfin live stream backed by a relay session. Sharing is enabled: viewers of the same stream and variant reuse
/// the same live stream, and the relay keeps a single origin connection.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Implements Jellyfin's ILiveStream.")]
public sealed class RelayLiveStream : ILiveStream
{
    private readonly RelayService _relay;
    private readonly RelayClaims _claims;
    private readonly string _token;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Sessions are owned and disposed by RelayService; this object only holds a reference count.")]
    private RelaySession? _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayLiveStream"/> class.
    /// </summary>
    /// <param name="relay">The relay.</param>
    /// <param name="claims">The claims.</param>
    /// <param name="token">The token.</param>
    /// <param name="mediaSource">The media source.</param>
    public RelayLiveStream(RelayService relay, RelayClaims claims, string token, MediaSourceInfo mediaSource)
    {
        _relay = relay;
        _claims = claims;
        _token = token;
        MediaSource = mediaSource;
        OriginalStreamId = claims.SessionKey;

        // Jellyfin indexes open live streams by this id (it adds its provider prefix): it must be set by the provider.
        MediaSource.LiveStreamId = UniqueId;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; }

    /// <inheritdoc />
    public string TunerHostId => "livehub";

    /// <inheritdoc />
    public bool EnableStreamSharing => true;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public async Task Open(CancellationToken openCancellationToken)
    {
        _session = _relay.Acquire(_claims);
        ConsumerCount = Math.Max(1, ConsumerCount);
        try
        {
            // Resolve now: errors reach the client immediately and the first playlist request is fast.
            var mode = await _relay.PrepareAsync(_session, openCancellationToken).ConfigureAwait(false);
            MediaSource.Path = _relay.BuildUrl(_token, mode);
            MediaSource.Container = mode switch
            {
                RelayMode.Ts => "mpegts",
                RelayMode.File => "mp4",
                RelayMode.Dash => "dash",
                _ => "hls",
            };
            MediaSource.IsInfiniteStream = mode != RelayMode.File;
            if (mode == RelayMode.Hls)
            {
                await _relay.GetMasterAsync(_session, openCancellationToken).ConfigureAwait(false);
            }
        }
        catch (RelayException ex)
        {
            _relay.Release(_claims.SessionKey, _claims.UserId);
            _session = null;
            throw new InvalidOperationException("Live Hub: " + ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public Task Close()
    {
        if (_session is not null)
        {
            _relay.Release(_claims.SessionKey, _claims.UserId);
            _session = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => Close().GetAwaiter().GetResult();

    /// <inheritdoc />
    public Stream GetStream()
    {
        if (_session?.Mode != RelayMode.Ts)
        {
            throw new NotSupportedException("Only MPEG-TS Live Hub streams can be read directly; use MediaSource.Path.");
        }

        var reader = _relay.SubscribeTsAsync(_session, CancellationToken.None).GetAwaiter().GetResult();
        return new ChannelReaderStream(reader, () => _session?.Fanout?.Unsubscribe(reader));
    }

    private sealed class ChannelReaderStream : Stream
    {
        private readonly ChannelReader<ReadOnlyMemory<byte>> _reader;
        private readonly Action _onDispose;
        private ReadOnlyMemory<byte> _current;

        public ChannelReaderStream(ChannelReader<ReadOnlyMemory<byte>> reader, Action onDispose)
        {
            _reader = reader;
            _onDispose = onDispose;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.IsEmpty)
            {
                if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                _reader.TryRead(out _current);
            }

            var n = Math.Min(buffer.Length, _current.Length);
            _current[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _onDispose();
            }

            base.Dispose(disposing);
        }
    }
}
