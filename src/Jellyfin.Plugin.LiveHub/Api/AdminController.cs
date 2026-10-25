using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Wizard;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>
/// Administration API: providers, import/export, templates, wizard. Requires an administrator.
/// Definitions travel as raw JSON (camelCase, schema v1) and are parsed strictly.
/// </summary>
[ApiController]
[Route("LiveHub/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public partial class AdminController : ControllerBase
{
    private readonly DefinitionStore _store;
    private readonly WizardService _wizard;
    private readonly PlaybackTester _tester;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="store">The definition store.</param>
    /// <param name="wizard">The wizard.</param>
    /// <param name="tester">The playback tester.</param>
    public AdminController(DefinitionStore store, WizardService wizard, PlaybackTester tester)
    {
        _store = store;
        _wizard = wizard;
        _tester = tester;
    }

    /// <summary>Lists the providers (secrets masked).</summary>
    /// <returns>The providers.</returns>
    [HttpGet("Providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<JsonNode> ListProviders()
    {
        var array = new JsonArray();
        foreach (var def in _store.List())
        {
            array.Add(ToNode(DefinitionStore.Masked(def)));
        }

        return Ok(array);
    }

    /// <summary>Gets a provider (secrets masked).</summary>
    /// <param name="id">The id.</param>
    /// <returns>The definition.</returns>
    [HttpGet("Providers/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<JsonNode> GetProvider([FromRoute] string id)
    {
        var def = _store.Get(id);
        return def is null ? NotFound() : Ok(ToNode(DefinitionStore.Masked(def)));
    }

    /// <summary>Creates or updates a provider. Body: the definition JSON.</summary>
    /// <param name="body">The definition.</param>
    /// <returns>The validation result.</returns>
    [HttpPost("Providers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> SaveProvider([FromBody] JsonNode body)
    {
        var def = Parse(body, out var error);
        if (def is null)
        {
            return BadRequest(new { Errors = new[] { new ValidationIssue { Message = error ?? "Invalid JSON." } } });
        }

        var result = _store.Save(def);
        if (!result.IsValid)
        {
            return BadRequest(new { result.Errors, result.Warnings });
        }

        return Ok(new { Id = def.Id, result.Warnings });
    }

    /// <summary>Deletes a provider.</summary>
    /// <param name="id">The id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Providers/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteProvider([FromRoute] string id) => _store.Delete(id) ? NoContent() : NotFound();

    /// <summary>Enables or disables a provider.</summary>
    /// <param name="id">The id.</param>
    /// <param name="enabled">The new state.</param>
    /// <returns>No content.</returns>
    [HttpPost("Providers/{id}/Enabled")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetEnabled([FromRoute] string id, [FromQuery] bool enabled)
    {
        var def = _store.Get(id);
        if (def is null)
        {
            return NotFound();
        }

        var copy = DefinitionSerializer.Clone(def);
        copy.Enabled = enabled;
        var result = _store.Save(copy);
        return result.IsValid ? NoContent() : BadRequest(new { result.Errors });
    }

    /// <summary>Exports a provider as a shareable JSON file (secrets removed).</summary>
    /// <param name="id">The id.</param>
    /// <returns>The file.</returns>
    [HttpGet("Providers/{id}/Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Export([FromRoute] string id)
    {
        var def = _store.Get(id);
        if (def is null)
        {
            return NotFound();
        }

        return File(Encoding.UTF8.GetBytes(DefinitionStore.Export(def)), MediaTypeNames.Application.Json, def.Id + ".livehub.json");
    }

    /// <summary>Imports a definition; the id is made unique unless overwrite is true.</summary>
    /// <param name="body">The definition JSON.</param>
    /// <param name="overwrite">Whether an existing provider with the same id is replaced.</param>
    /// <returns>The saved id and warnings.</returns>
    [HttpPost("Import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> Import([FromBody] JsonNode body, [FromQuery] bool overwrite = false)
    {
        var def = Parse(body, out var error);
        if (def is null)
        {
            return BadRequest(new { Errors = new[] { new ValidationIssue { Message = error ?? "Invalid JSON." } } });
        }

        if (!overwrite && _store.Get(def.Id) is not null)
        {
            def.Id = _store.UniqueId(def.Id);
        }

        var result = _store.Save(def);
        return result.IsValid ? Ok(new { Id = def.Id, result.Warnings }) : BadRequest(new { result.Errors, result.Warnings });
    }

    /// <summary>Validates a definition without saving it.</summary>
    /// <param name="body">The definition JSON.</param>
    /// <returns>The validation result.</returns>
    [HttpPost("Validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Validate([FromBody] JsonNode body)
    {
        var def = Parse(body, out var error);
        if (def is null)
        {
            return Ok(new { Valid = false, Errors = new[] { new ValidationIssue { Message = error ?? "Invalid JSON." } }, Warnings = Array.Empty<ValidationIssue>() });
        }

        var result = DefinitionValidator.Validate(_store.RestoreMaskedSecrets(def));
        return Ok(new { Valid = result.IsValid, result.Errors, result.Warnings });
    }

    /// <summary>Gets the template library.</summary>
    /// <returns>The templates.</returns>
    [HttpGet("Templates")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<DefinitionTemplate>> Templates() => Ok(TemplateLibrary.All());

    /// <summary>Gets provider.schema.json.</summary>
    /// <returns>The schema.</returns>
    [HttpGet("Schema")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Schema() => Content(TemplateLibrary.Schema(), MediaTypeNames.Application.Json);

    /// <summary>Wizard step 1-2: downloads a sample, recognises the format and proposes a mapping.</summary>
    /// <param name="input">The wizard input.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The detection (definition as raw JSON in DefinitionJson).</returns>
    [HttpPost("Wizard/Detect")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> Detect([FromBody] WizardInput input, CancellationToken cancellationToken)
    {
        var d = await _wizard.DetectAsync(input, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            d.Format,
            d.Summary,
            Definition = d.Definition is null ? null : ToNode(d.Definition),
            d.Fields,
            d.StreamFields,
            d.Operations,
            d.Warnings,
            d.Sample,
            d.HttpStatus,
        });
    }

    /// <summary>Wizard step 3: runs a definition in preview mode. Body: { "definition": {...}, "sample": "optional" }.</summary>
    /// <param name="body">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The preview.</returns>
    [HttpPost("Wizard/Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WizardPreview>> Preview([FromBody] JsonNode body, CancellationToken cancellationToken)
    {
        var def = Parse(body?["definition"] ?? body, out var error);
        if (def is null)
        {
            var invalid = new WizardPreview();
            invalid.ValidationErrors.Add(new ValidationIssue { Message = error ?? "Invalid JSON." });
            return Ok(invalid);
        }

        var sample = body?["sample"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return Ok(await _wizard.PreviewAsync(def, sample, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>"Test playback": resolves a stream and downloads a playlist and a first segment, with a diagnosis.</summary>
    /// <param name="body">{ definition?, providerId?, itemKey?, streamId?, isChannel?, url? }.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The result.</returns>
    [HttpPost("TestPlayback")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PlaybackTestResult>> TestPlayback([FromBody] JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var request = new PlaybackTestRequest
        {
            ProviderId = body["providerId"]?.GetValue<string>(),
            ItemKey = body["itemKey"]?.GetValue<string>(),
            StreamId = body["streamId"]?.GetValue<string>(),
            IsChannel = body["isChannel"]?.GetValue<bool>() ?? false,
            Url = body["url"]?.GetValue<string>(),
        };
        if (body["definition"] is { } node)
        {
            request.Definition = Parse(node, out var error);
            if (request.Definition is null)
            {
                return BadRequest(error);
            }
        }

        return Ok(await _tester.TestAsync(request, cancellationToken).ConfigureAwait(false));
    }

    private static JsonNode ToNode(ProviderDefinition def) => JsonNode.Parse(DefinitionSerializer.Serialize(def))!;

    private static ProviderDefinition? Parse(JsonNode? node, out string? error)
    {
        if (node is null)
        {
            error = "Missing body.";
            return null;
        }

        var text = node is JsonValue value && value.TryGetValue<string>(out var str) ? str : node.ToJsonString();
        return DefinitionSerializer.TryParse(text, out error);
    }
}
