using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveHub.Diagnostics;

/// <summary>Information about an active relay stream.</summary>
public sealed class ActiveStreamInfo
{
    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name.</summary>
    public string? User { get; set; }

    /// <summary>Gets or sets the origin host (no path, no credentials).</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the stream started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Gets or sets the last activity.</summary>
    public DateTimeOffset LastActivity { get; set; }

    /// <summary>Gets or sets the bytes relayed.</summary>
    public long Bytes { get; set; }

    /// <summary>Gets or sets the failovers performed.</summary>
    public int Failovers { get; set; }

    /// <summary>Gets or sets the number of consumers.</summary>
    public int Consumers { get; set; }
}

/// <summary>Provides the active relay streams to diagnostics.</summary>
public interface IActiveStreamsSource
{
    /// <summary>Gets the active streams.</summary>
    /// <returns>The streams.</returns>
    IReadOnlyList<ActiveStreamInfo> ActiveStreams();
}
