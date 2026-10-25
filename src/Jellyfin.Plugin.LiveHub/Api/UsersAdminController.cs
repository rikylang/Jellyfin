using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>Per-user source access (administrators only).</summary>
[ApiController]
[Route("LiveHub/Admin/Users")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class UsersAdminController : ControllerBase
{
    private readonly UserSettingsService _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="UsersAdminController"/> class.
    /// </summary>
    /// <param name="settings">Settings service.</param>
    public UsersAdminController(UserSettingsService settings) => _settings = settings;

    /// <summary>Gets every user with its access settings.</summary>
    /// <returns>The users.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<UserAccessDto>> Get() => Ok(_settings.GetAccess());

    /// <summary>Saves the access settings.</summary>
    /// <param name="access">The users.</param>
    /// <returns>No content.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Save([FromBody] List<UserAccessDto> access)
    {
        _settings.SaveAccess(access);
        return NoContent();
    }
}
