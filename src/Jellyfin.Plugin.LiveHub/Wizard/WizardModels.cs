using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;

namespace Jellyfin.Plugin.LiveHub.Wizard;

/// <summary>Detected source format.</summary>
public enum SourceFormat
{
    /// <summary>Unknown.</summary>
    Unknown,

    /// <summary>REST JSON API.</summary>
    RestJson,

    /// <summary>IPTV M3U playlist.</summary>
    M3u,

    /// <summary>XMLTV guide (EPG only).</summary>
    Xmltv,

    /// <summary>Xtream Codes API.</summary>
    Xtream,

    /// <summary>Direct HLS/DASH/MP4/TS stream.</summary>
    DirectStream,

    /// <summary>Web page (embed player).</summary>
    WebPage,

    /// <summary>OpenAPI / Swagger specification.</summary>
    OpenApi,
}

/// <summary>Wizard input ("Add API").</summary>
public sealed class WizardInput
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a pasted sample response (used instead of fetching).</summary>
    public string? SampleResponse { get; set; }

    /// <summary>Gets or sets pasted documentation text (field names are used as hints).</summary>
    public string? Documentation { get; set; }

    /// <summary>Gets or sets a pasted OpenAPI/Swagger specification (JSON or YAML).</summary>
    public string? OpenApiSpec { get; set; }

    /// <summary>Gets or sets the authentication to use while probing.</summary>
    public AuthSpec? Auth { get; set; }

    /// <summary>Gets or sets the secrets referenced by <see cref="Auth"/> (plain text, never stored by the probe).</summary>
    public Dictionary<string, string>? Secrets { get; set; }

    /// <summary>Gets or sets extra headers.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Gets or sets a value indicating whether private network targets are allowed.</summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>Gets or sets the egress profile used for probing.</summary>
    public string? EgressProfile { get; set; }

    /// <summary>Gets or sets the OpenAPI operation chosen for events ("GET /v2/schedule").</summary>
    public string? OpenApiOperation { get; set; }
}

/// <summary>How a target field was mapped by the suggester.</summary>
public sealed class FieldSuggestion
{
    /// <summary>Gets or sets the target field (title, start...).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the JSONPath.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the confidence 0..1.</summary>
    public double Confidence { get; set; }

    /// <summary>Gets or sets a sample value.</summary>
    public string? Sample { get; set; }

    /// <summary>Gets or sets the detected format (dates).</summary>
    public string? Format { get; set; }
}

/// <summary>An operation of an OpenAPI specification.</summary>
public sealed class OpenApiOperation
{
    /// <summary>Gets or sets the method.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Gets or sets the path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the operation id.</summary>
    public string? OperationId { get; set; }

    /// <summary>Gets or sets the summary.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets the query parameter names.</summary>
    public List<string> QueryParameters { get; } = new();

    /// <summary>Gets or sets the suggested role (events, categories, streams, channels).</summary>
    public string? SuggestedRole { get; set; }

    /// <summary>Gets the key ("GET /path").</summary>
    public string Key => Method + " " + Path;
}

/// <summary>A parsed OpenAPI document.</summary>
public sealed class OpenApiDocument
{
    /// <summary>Gets or sets the title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets the detected authentication.</summary>
    public AuthSpec? Auth { get; set; }

    /// <summary>Gets the operations.</summary>
    public List<OpenApiOperation> Operations { get; } = new();
}

/// <summary>Result of the detection step.</summary>
public sealed class WizardDetection
{
    /// <summary>Gets or sets the format.</summary>
    public SourceFormat Format { get; set; }

    /// <summary>Gets or sets a human readable summary.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the proposed definition (secrets masked when returned to the UI).</summary>
    public ProviderDefinition? Definition { get; set; }

    /// <summary>Gets the field suggestions.</summary>
    public List<FieldSuggestion> Fields { get; } = new();

    /// <summary>Gets the stream field suggestions.</summary>
    public List<FieldSuggestion> StreamFields { get; } = new();

    /// <summary>Gets the OpenAPI operations.</summary>
    public List<OpenApiOperation> Operations { get; } = new();

    /// <summary>Gets the warnings.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Gets or sets a snippet of the sample (first KB).</summary>
    public string? Sample { get; set; }

    /// <summary>Gets or sets the detected HTTP status of the probe.</summary>
    public int? HttpStatus { get; set; }
}

/// <summary>A mapping proposed for a JSON body.</summary>
public sealed class MappingSuggestion
{
    /// <summary>Gets or sets the items root path.</summary>
    public string? Root { get; set; }

    /// <summary>Gets the field mapping.</summary>
    public FieldMap Fields { get; } = new();

    /// <summary>Gets or sets the participants mapping.</summary>
    public ParticipantsSpec? Participants { get; set; }

    /// <summary>Gets or sets the inline streams.</summary>
    public InlineStreamsSpec? Streams { get; set; }

    /// <summary>Gets or sets the pagination.</summary>
    public PaginationSpec? Pagination { get; set; }

    /// <summary>Gets or sets the role (events or channels).</summary>
    public string Role { get; set; } = "events";

    /// <summary>Gets or sets the number of items in the chosen array.</summary>
    public int ItemCount { get; set; }

    /// <summary>Gets or sets the item leaf used as stream URL (excluded from event fields).</summary>
    public string? StreamUrlLeaf { get; set; }

    /// <summary>Gets the event field report.</summary>
    public List<FieldSuggestion> Report { get; } = new();

    /// <summary>Gets the stream field report.</summary>
    public List<FieldSuggestion> StreamReport { get; } = new();

    /// <summary>Gets the warnings.</summary>
    public List<string> Warnings { get; } = new();
}

/// <summary>A previewed event row.</summary>
public sealed class PreviewEvent
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the category.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the competition.</summary>
    public string? Competition { get; set; }

    /// <summary>Gets or sets the start.</summary>
    public DateTimeOffset? Start { get; set; }

    /// <summary>Gets or sets the status.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the country.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the image.</summary>
    public string? Image { get; set; }

    /// <summary>Gets or sets the number of streams.</summary>
    public int StreamCount { get; set; }

    /// <summary>Gets or sets a value indicating whether streams are loaded lazily.</summary>
    public bool LazyStreams { get; set; }

    /// <summary>Gets the streams.</summary>
    public List<PreviewStreamInfo> Streams { get; } = new();

    /// <summary>Gets the problems (missing / invalid fields).</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Gets or sets a value indicating whether this is a 24/7 channel.</summary>
    public bool IsChannel { get; set; }
}

/// <summary>A previewed stream.</summary>
public sealed class PreviewStreamInfo
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the redacted URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the quality.</summary>
    public string? Quality { get; set; }

    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the number of required headers.</summary>
    public int Headers { get; set; }
}

/// <summary>Result of a preview.</summary>
public sealed class WizardPreview
{
    /// <summary>Gets or sets a value indicating whether the definition is valid.</summary>
    public bool Valid { get; set; }

    /// <summary>Gets the validation errors.</summary>
    public List<ValidationIssue> ValidationErrors { get; } = new();

    /// <summary>Gets the validation warnings.</summary>
    public List<ValidationIssue> ValidationWarnings { get; } = new();

    /// <summary>Gets the rows (events and channels, max 100).</summary>
    public List<PreviewEvent> Rows { get; } = new();

    /// <summary>Gets the counters.</summary>
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the requests performed.</summary>
    public List<RequestLog> Requests { get; } = new();

    /// <summary>Gets the errors.</summary>
    public List<string> Errors { get; } = new();

    /// <summary>Gets the warnings.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Gets raw sample items per endpoint (JSON text).</summary>
    public Dictionary<string, string> Samples { get; } = new(StringComparer.Ordinal);
}
