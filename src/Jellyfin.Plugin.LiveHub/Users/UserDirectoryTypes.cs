using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>A Jellyfin user (id and name).</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The name.</param>
public sealed record DirectoryUser(Guid Id, string Name);

/// <summary>Lists the Jellyfin users (abstracted for tests).</summary>
public interface IUserDirectory
{
    /// <summary>Gets the users.</summary>
    /// <returns>The users.</returns>
    IReadOnlyList<DirectoryUser> Users();
}

/// <summary>User directory backed by Jellyfin's user manager.</summary>
public sealed class JellyfinUserDirectory : IUserDirectory
{
    private readonly IUserManager _users;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinUserDirectory"/> class.
    /// </summary>
    /// <param name="users">The user manager.</param>
    public JellyfinUserDirectory(IUserManager users) => _users = users;

    /// <inheritdoc />
    public IReadOnlyList<DirectoryUser> Users()
        => _users.GetUsers().Select(u => new DirectoryUser(u.Id, u.Username)).OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
}

/// <summary>Access settings of a user as edited in the admin page.</summary>
public sealed class UserAccessDto
{
    /// <summary>Gets or sets the user id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user only sees the allowed providers.</summary>
    public bool Restricted { get; set; }

    /// <summary>Gets or sets the allowed providers.</summary>
    public List<string> AllowedProviders { get; set; } = new();
}

/// <summary>A choice offered by the preferences page.</summary>
/// <param name="Id">The value.</param>
/// <param name="Name">The label.</param>
public sealed record PreferenceOption(string Id, string Name);

/// <summary>Everything the personal preferences page needs.</summary>
public sealed class UserPreferencesPage
{
    /// <summary>Gets or sets the preferences.</summary>
    public UserPreferences Preferences { get; set; } = new();

    /// <summary>Gets the providers the user may see.</summary>
    public List<PreferenceOption> Providers { get; } = new();

    /// <summary>Gets the categories.</summary>
    public List<string> Categories { get; } = new();

    /// <summary>Gets the languages.</summary>
    public List<PreferenceOption> Languages { get; } = new();

    /// <summary>Gets the countries.</summary>
    public List<PreferenceOption> Countries { get; } = new();

    /// <summary>Gets the 24/7 channels.</summary>
    public List<PreferenceOption> Channels { get; } = new();

    /// <summary>Gets the time zones.</summary>
    public List<string> TimeZones { get; } = new();

    /// <summary>Gets or sets the effective time zone.</summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether notifications are enabled on the server.</summary>
    public bool NotificationsAvailable { get; set; }
}
