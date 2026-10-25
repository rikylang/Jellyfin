using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>An active client session.</summary>
/// <param name="Id">The session id.</param>
/// <param name="UserId">The user.</param>
/// <param name="Playing">Whether something is playing (never disturbed).</param>
/// <param name="CanDisplayMessage">Whether the client supports DisplayMessage.</param>
public sealed record ActiveSession(string Id, Guid UserId, bool Playing, bool CanDisplayMessage);

/// <summary>Sends messages to client sessions (abstracted for tests).</summary>
public interface ISessionSink
{
    /// <summary>Gets the active sessions.</summary>
    /// <returns>The sessions.</returns>
    IReadOnlyList<ActiveSession> Sessions();

    /// <summary>Shows a message on a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="header">Header.</param>
    /// <param name="text">Text.</param>
    /// <param name="timeoutMs">Display time.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SendAsync(string sessionId, string header, string text, long timeoutMs, CancellationToken cancellationToken);
}

/// <summary>Session sink backed by Jellyfin's session manager (native DisplayMessage).</summary>
public sealed class JellyfinSessionSink : ISessionSink
{
    private readonly ISessionManager _sessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinSessionSink"/> class.
    /// </summary>
    /// <param name="sessions">The session manager.</param>
    public JellyfinSessionSink(ISessionManager sessions) => _sessions = sessions;

    /// <inheritdoc />
    public IReadOnlyList<ActiveSession> Sessions()
        => _sessions.Sessions
            .Where(s => s.IsActive && !s.UserId.Equals(Guid.Empty))
            .Select(s => new ActiveSession(s.Id, s.UserId, s.NowPlayingItem is not null, s.SupportedCommands.Contains(GeneralCommandType.DisplayMessage)))
            .ToList();

    /// <inheritdoc />
    public Task SendAsync(string sessionId, string header, string text, long timeoutMs, CancellationToken cancellationToken)
        => _sessions.SendMessageCommand(null, sessionId, new MessageCommand { Header = header, Text = text, TimeoutMs = timeoutMs }, cancellationToken);
}
