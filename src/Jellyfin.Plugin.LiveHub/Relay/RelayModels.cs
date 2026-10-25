using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Http;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>A buffered segment.</summary>
/// <param name="Status">HTTP status of the origin.</param>
/// <param name="Data">The body.</param>
/// <param name="ContentType">The content type.</param>
public sealed record CachedSegment(int Status, byte[] Data, string? ContentType);

/// <summary>Thrown by the relay; mapped to an HTTP status by the controller.</summary>
public sealed class RelayException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RelayException"/> class.
    /// </summary>
    public RelayException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public RelayException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public RelayException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayException"/> class.
    /// </summary>
    /// <param name="status">The HTTP status for the client.</param>
    /// <param name="message">The message.</param>
    /// <param name="hint">Diagnosis key.</param>
    public RelayException(int status, string message, string? hint = null)
        : base(message)
    {
        Status = status;
        Hint = hint;
    }

    /// <summary>Gets the HTTP status for the client.</summary>
    public int Status { get; } = 502;

    /// <summary>Gets the diagnosis key.</summary>
    public string? Hint { get; }
}

/// <summary>Provides the base URL at which the local server (and ffmpeg) reaches the relay.</summary>
public interface IRelayBaseUrl
{
    /// <summary>Gets the base URL (scheme, host, port and Jellyfin base path; no trailing slash).</summary>
    string BaseUrl { get; }
}

/// <summary>Base URL from the Jellyfin server (local access, plain HTTP so ffmpeg never deals with certificates).</summary>
public sealed class ServerRelayBaseUrl : IRelayBaseUrl
{
    private readonly IServerApplicationHost _host;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerRelayBaseUrl"/> class.
    /// </summary>
    /// <param name="host">The server host.</param>
    public ServerRelayBaseUrl(IServerApplicationHost host) => _host = host;

    /// <inheritdoc />
    public string BaseUrl => _host.GetApiUrlForLocalAccess(null, false).TrimEnd('/');
}

/// <summary>A body stream that counts relayed bytes and releases the origin response when disposed.</summary>
internal sealed class RelayedStream : Stream
{
    private readonly StreamingResponse _response;
    private readonly RelaySession _session;

    public RelayedStream(StreamingResponse response, RelaySession session)
    {
        _response = response;
        _session = session;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _response.Stream.Read(buffer, offset, count);
        _session.AddBytes(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _response.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _session.AddBytes(n);
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

    public override async ValueTask DisposeAsync()
    {
        await _response.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _response.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
