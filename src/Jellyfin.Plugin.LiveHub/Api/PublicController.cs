using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.LiveHub.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>
/// Public, read-only endpoints (UI strings, plugin info).
/// </summary>
[ApiController]
[Route("LiveHub")]
[Produces(MediaTypeNames.Application.Json)]
public class PublicController : ControllerBase
{
    private readonly LocalizationService _localization;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublicController"/> class.
    /// </summary>
    /// <param name="localization">The localization service.</param>
    public PublicController(LocalizationService localization)
    {
        _localization = localization;
    }

    /// <summary>Gets the UI strings for a language (it, en).</summary>
    /// <param name="lang">The language.</param>
    /// <returns>The strings.</returns>
    [HttpGet("i18n/{lang}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyDictionary<string, string>> GetStrings([FromRoute] string lang)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(_localization.GetStrings(lang));
    }

    /// <summary>Gets basic plugin information.</summary>
    /// <returns>Name and version.</returns>
    [HttpGet("info")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetInfo()
    {
        var plugin = Plugin.Instance;
        return Ok(new
        {
            Name = Plugin.PluginName,
            Version = plugin?.Version.ToString() ?? "0.0.0.0",
            UiLanguage = plugin?.Configuration.UiLanguage ?? "auto",
        });
    }
}
