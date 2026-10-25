using System;
using System.Net.Mime;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>Personal preferences of the signed-in user (valid on every device).</summary>
[ApiController]
[Route("LiveHub/User")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class UserController : ControllerBase
{
    private readonly UserSettingsService _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserController"/> class.
    /// </summary>
    /// <param name="settings">Settings service.</param>
    public UserController(UserSettingsService settings) => _settings = settings;

    /// <summary>Gets the preferences and the available choices.</summary>
    /// <returns>The page data.</returns>
    [HttpGet("Preferences")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<UserPreferencesPage> Get()
    {
        var user = CurrentUser();
        return user == Guid.Empty ? Unauthorized() : Ok(_settings.GetPage(user));
    }

    /// <summary>Saves the preferences.</summary>
    /// <param name="prefs">The preferences.</param>
    /// <returns>The saved preferences.</returns>
    [HttpPost("Preferences")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<UserPreferences> Save([FromBody] UserPreferences prefs)
    {
        var user = CurrentUser();
        return user == Guid.Empty ? Unauthorized() : Ok(_settings.Save(user, prefs));
    }

    private Guid CurrentUser() => Guid.TryParse(User.FindFirst(LiveHubMediaSourceProvider.UserIdClaim)?.Value, out var id) ? id : Guid.Empty;
}
