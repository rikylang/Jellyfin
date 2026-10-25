using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Security;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>Egress (proxy/VPN) profiles, connection checks and gluetun integration. Administrators only.</summary>
[ApiController]
[Route("LiveHub/Admin/Egress")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public partial class EgressController : ControllerBase
{
    private readonly ConfigAccessor _config;
    private readonly EgressManager _egress;
    private readonly EgressDiagnostics _diagnostics;
    private readonly SecretProtector _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressController"/> class.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="egress">Egress manager.</param>
    /// <param name="diagnostics">Diagnostics.</param>
    /// <param name="secrets">Secrets.</param>
    public EgressController(ConfigAccessor config, EgressManager egress, EgressDiagnostics diagnostics, SecretProtector secrets)
    {
        _config = config;
        _egress = egress;
        _diagnostics = diagnostics;
        _secrets = secrets;
    }

    /// <summary>Applies an egress save request to a configuration (validation, secret handling).</summary>
    /// <param name="cfg">The configuration (updated).</param>
    /// <param name="request">The request.</param>
    /// <param name="secrets">Secret protector.</param>
    /// <returns>The errors (empty when saved).</returns>
    public static List<string> Apply(PluginConfiguration cfg, EgressSaveRequest request, SecretProtector secrets)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(secrets);
        var errors = new List<string>();
        var profiles = new List<EgressProfile>();
        foreach (var p in request.Profiles ?? new List<EgressProfile>())
        {
            p.Id = (p.Id ?? string.Empty).Trim();
            if (!IdRegex().IsMatch(p.Id))
            {
                errors.Add($"Invalid profile id '{p.Id}' (letters, digits, '-' and '_').");
                continue;
            }

            if (profiles.Exists(x => x.Id == p.Id))
            {
                errors.Add($"Duplicate profile id '{p.Id}'.");
                continue;
            }

            if (p.Id == EgressProfile.DirectId)
            {
                p.Type = EgressType.Direct;
            }

            if (p.Type != EgressType.Direct && (string.IsNullOrWhiteSpace(p.Host) || p.Port is <= 0 or > 65535))
            {
                errors.Add($"Profile '{p.Id}': host and port are required.");
            }

            var existing = cfg.EgressProfiles.Find(x => x.Id == p.Id);
            p.Password = KeepOrProtect(p.Password, existing?.Password, secrets);
            p.GluetunApiKey = KeepOrProtect(p.GluetunApiKey, existing?.GluetunApiKey, secrets);
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name.Trim();
            p.Host = (p.Host ?? string.Empty).Trim();
            p.ExpectedCountry = LocaleNormalizer.NormalizeCountry(p.ExpectedCountry) ?? string.Empty;
            p.HealthCheckSeconds = Math.Clamp(p.HealthCheckSeconds <= 0 ? 60 : p.HealthCheckSeconds, 10, 3600);
            profiles.Add(p);
        }

        if (!profiles.Exists(p => p.Id == EgressProfile.DirectId))
        {
            profiles.Insert(0, EgressProfile.CreateDirect());
        }

        var regions = new List<RegionEgressRule>();
        foreach (var r in request.RegionEgress ?? new List<RegionEgressRule>())
        {
            var country = LocaleNormalizer.NormalizeCountry(r.Country);
            if (country is null || !profiles.Exists(p => p.Id == r.ProfileId))
            {
                errors.Add($"Invalid region rule '{r.Country} = {r.ProfileId}'.");
                continue;
            }

            regions.Add(new RegionEgressRule { Country = country, ProfileId = r.ProfileId });
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        cfg.EgressProfiles = profiles;
        cfg.RegionEgress = regions;
        cfg.DefaultEgressProfileId = profiles.Exists(p => p.Id == request.DefaultProfileId) ? request.DefaultProfileId! : EgressProfile.DirectId;
        return errors;
    }

    /// <summary>Gets the profiles (secrets masked), their health and the region rules.</summary>
    /// <returns>The data.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Get()
    {
        var cfg = _config.Current;
        var profiles = cfg.EgressProfiles.Count > 0 ? cfg.EgressProfiles : new List<EgressProfile> { EgressProfile.CreateDirect() };
        return Ok(new
        {
            Profiles = profiles.Select(p => new
            {
                p.Id,
                p.Name,
                Type = p.Type.ToString(),
                p.Host,
                p.Port,
                p.Username,
                Password = string.IsNullOrEmpty(p.Password) ? string.Empty : SecretProtector.Mask,
                p.ExpectedCountry,
                p.KillSwitch,
                p.HealthCheckSeconds,
                p.GluetunControlUrl,
                GluetunApiKey = string.IsNullOrEmpty(p.GluetunApiKey) ? string.Empty : SecretProtector.Mask,
                Route = EgressManager.Describe(p),
            }),
            cfg.DefaultEgressProfileId,
            cfg.RegionEgress,
            cfg.IpCheckUrl,
            Health = profiles.ToDictionary(p => p.Id, p => _egress.GetHealth(p.Id), StringComparer.Ordinal),
        });
    }

    /// <summary>Saves the profiles.</summary>
    /// <param name="request">The request.</param>
    /// <returns>No content, or the validation errors.</returns>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult Save([FromBody] EgressSaveRequest request)
    {
        var errors = Apply(_config.Current, request, _secrets);
        if (errors.Count > 0)
        {
            return BadRequest(new { Errors = errors });
        }

        _config.Save();
        return NoContent();
    }

    /// <summary>"Verify connection": public IP and exit country seen through a profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    [HttpPost("{id}/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EgressCheckResult>> Check([FromRoute] string id, CancellationToken cancellationToken)
        => Ok(await _diagnostics.CheckAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>Gets the gluetun tunnel status of a profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The status.</returns>
    [HttpGet("{id}/Gluetun")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<GluetunStatus>> Gluetun([FromRoute] string id, CancellationToken cancellationToken)
        => Ok(await _diagnostics.GluetunAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>Asks gluetun to switch country (when supported; otherwise returns instructions).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="body">{ "Country": "Italy" }.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The outcome.</returns>
    [HttpPost("{id}/Gluetun/Country")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GluetunCountry([FromRoute] string id, [FromBody] JsonNode body, CancellationToken cancellationToken)
    {
        var country = body?["Country"]?.GetValue<string>() ?? body?["country"]?.GetValue<string>() ?? string.Empty;
        var (ok, message) = await _diagnostics.SetGluetunCountryAsync(id, country, cancellationToken).ConfigureAwait(false);
        return Ok(new { Ok = ok, Message = message });
    }

    private static string KeepOrProtect(string? incoming, string? existing, SecretProtector secrets)
    {
        if (string.Equals(incoming, SecretProtector.Mask, StringComparison.Ordinal))
        {
            return existing ?? string.Empty;
        }

        return string.IsNullOrEmpty(incoming) ? string.Empty : secrets.Protect(incoming);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();
}
