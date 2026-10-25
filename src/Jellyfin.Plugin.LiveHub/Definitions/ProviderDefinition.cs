using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LiveHub.Definitions;

/// <summary>Provider kind.</summary>
public enum ProviderKind
{
    /// <summary>REST JSON API with field mapping.</summary>
    Rest,

    /// <summary>M3U/M3U8 playlist (+ XMLTV).</summary>
    M3u,

    /// <summary>Xtream Codes API.</summary>
    Xtream,

    /// <summary>A single direct stream URL.</summary>
    Direct,
}

/// <summary>Authentication type.</summary>
public enum AuthType
{
    /// <summary>No authentication.</summary>
    None,

    /// <summary>API key in a header or query parameter.</summary>
    ApiKey,

    /// <summary>Static bearer token.</summary>
    Bearer,

    /// <summary>HTTP basic.</summary>
    Basic,

    /// <summary>OAuth2 client credentials grant.</summary>
    Oauth2ClientCredentials,

    /// <summary>Static cookie.</summary>
    Cookie,
}

/// <summary>Where an API key is sent.</summary>
public enum KeyLocation
{
    /// <summary>HTTP header.</summary>
    Header,

    /// <summary>Query string parameter.</summary>
    Query,
}

/// <summary>Pagination strategy.</summary>
public enum PaginationType
{
    /// <summary>Single request.</summary>
    None,

    /// <summary>Page number.</summary>
    Page,

    /// <summary>Item offset.</summary>
    Offset,

    /// <summary>Opaque cursor read from the body.</summary>
    Cursor,

    /// <summary>Full next URL read from the body.</summary>
    NextUrl,

    /// <summary>RFC 8288 Link header.</summary>
    LinkHeader,
}

/// <summary>Endpoint scope (drives the polling interval).</summary>
public enum EndpointScope
{
    /// <summary>Everything.</summary>
    All,

    /// <summary>Events on air.</summary>
    Live,

    /// <summary>Today's schedule.</summary>
    Today,

    /// <summary>Future events.</summary>
    Upcoming,
}

/// <summary>Request iteration.</summary>
public enum ForEachMode
{
    /// <summary>One request (plus pagination).</summary>
    None,

    /// <summary>One request per category.</summary>
    Category,

    /// <summary>One request per day of the date range.</summary>
    Date,
}

/// <summary>Body format.</summary>
public enum BodyFormat
{
    /// <summary>Detected from content type and body.</summary>
    Auto,

    /// <summary>JSON.</summary>
    Json,

    /// <summary>M3U playlist.</summary>
    M3u,

    /// <summary>XMLTV guide.</summary>
    Xmltv,
}

/// <summary>Declared stream type.</summary>
public enum StreamTypeSpec
{
    /// <summary>Detect.</summary>
    Auto,

    /// <summary>HLS.</summary>
    Hls,

    /// <summary>DASH.</summary>
    Dash,

    /// <summary>MP4.</summary>
    Mp4,

    /// <summary>MPEG-TS.</summary>
    Ts,

    /// <summary>Embed page.</summary>
    Embed,
}

/// <summary>Resolver strategy.</summary>
public enum ResolverStrategy
{
    /// <summary>Try direct, then extraction, then headless (if enabled).</summary>
    Auto,

    /// <summary>Only use URLs that are already playable.</summary>
    Direct,

    /// <summary>Force generic page extraction.</summary>
    Extract,

    /// <summary>Force the headless browser sidecar.</summary>
    Headless,
}

/// <summary>M3U interpretation.</summary>
public enum M3uMode
{
    /// <summary>Entries are 24/7 channels.</summary>
    Channels,

    /// <summary>Entries are events (time in the name).</summary>
    Events,
}

/// <summary>Xtream output container.</summary>
public enum XtreamOutput
{
    /// <summary>HLS.</summary>
    M3u8,

    /// <summary>MPEG-TS.</summary>
    Ts,
}

/// <summary>
/// A declarative Provider Definition (schema version 1). See schemas/provider.schema.json and docs/PROVIDERS.md.
/// </summary>
public sealed class ProviderDefinition
{
    /// <summary>Gets or sets the optional $schema reference.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>Gets or sets the schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether the provider is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the priority.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Gets or sets the kind.</summary>
    public ProviderKind Kind { get; set; }

    /// <summary>Gets or sets the egress profile id.</summary>
    public string? EgressProfile { get; set; }

    /// <summary>Gets or sets a value indicating whether private addresses are allowed.</summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>Gets or sets the defaults.</summary>
    public DefaultsSpec? Defaults { get; set; }

    /// <summary>Gets or sets the connection.</summary>
    public ConnectionSpec? Connection { get; set; }

    /// <summary>Gets or sets the secrets (encrypted at rest).</summary>
    public Dictionary<string, string>? Secrets { get; set; }

    /// <summary>Gets or sets the variables.</summary>
    public Dictionary<string, string>? Variables { get; set; }

    /// <summary>Gets or sets the categories endpoint.</summary>
    public EndpointSpec? Categories { get; set; }

    /// <summary>Gets or sets the event endpoints.</summary>
    public List<EndpointSpec>? Events { get; set; }

    /// <summary>Gets or sets the event detail endpoint.</summary>
    public EndpointSpec? EventDetail { get; set; }

    /// <summary>Gets or sets the stream chain.</summary>
    public StreamChainSpec? Streams { get; set; }

    /// <summary>Gets or sets the channel endpoints.</summary>
    public List<EndpointSpec>? Channels { get; set; }

    /// <summary>Gets or sets the M3U settings.</summary>
    public M3uSpec? M3u { get; set; }

    /// <summary>Gets or sets the Xtream settings.</summary>
    public XtreamSpec? Xtream { get; set; }

    /// <summary>Gets or sets the direct stream settings.</summary>
    public DirectSpec? Direct { get; set; }

    /// <summary>Gets or sets the resolver settings.</summary>
    public ResolverSpec? Resolver { get; set; }

    /// <summary>Gets or sets the refresh settings.</summary>
    public RefreshSpec? Refresh { get; set; }

    /// <summary>Gets or sets the category rules.</summary>
    public List<CategoryRuleSpec>? CategoryRules { get; set; }

    /// <summary>Gets or sets OpenAPI metadata.</summary>
    public OpenApiSpec? OpenApi { get; set; }
}

/// <summary>Defaults used when the API does not provide a value.</summary>
public sealed class DefaultsSpec
{
    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the country.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the time zone.</summary>
    public string? Timezone { get; set; }

    /// <summary>Gets or sets the category.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the event duration.</summary>
    public int? EventDurationMinutes { get; set; }

    /// <summary>Gets or sets the content rating.</summary>
    public string? ContentRating { get; set; }

    /// <summary>Gets or sets the tags.</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>Connection settings.</summary>
public sealed class ConnectionSpec
{
    /// <summary>Gets or sets the base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets extra headers.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Gets or sets the user agent.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Gets or sets the timeout.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>Gets or sets the response size limit.</summary>
    public long? MaxResponseBytes { get; set; }

    /// <summary>Gets or sets the rate limit.</summary>
    public RateLimitSpec? RateLimit { get; set; }

    /// <summary>Gets or sets the authentication.</summary>
    public AuthSpec? Auth { get; set; }
}

/// <summary>Rate limit.</summary>
public sealed class RateLimitSpec
{
    /// <summary>Gets or sets the sustained rate.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>Gets or sets the burst size.</summary>
    public int? Burst { get; set; }
}

/// <summary>Authentication.</summary>
public sealed class AuthSpec
{
    /// <summary>Gets or sets the type.</summary>
    public AuthType Type { get; set; }

    /// <summary>Gets or sets where the key goes.</summary>
    public KeyLocation? In { get; set; }

    /// <summary>Gets or sets the header/query/cookie name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the value template.</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets the user name template.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the password template.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets the OAuth2 token URL.</summary>
    public string? TokenUrl { get; set; }

    /// <summary>Gets or sets the OAuth2 client id.</summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the OAuth2 client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Gets or sets the OAuth2 scope.</summary>
    public string? Scope { get; set; }
}

/// <summary>An HTTP request template.</summary>
public sealed class RequestSpec
{
    /// <summary>Gets or sets the URL template.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the method (GET/POST).</summary>
    public string? Method { get; set; }

    /// <summary>Gets or sets extra headers.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Gets or sets extra query parameters.</summary>
    public Dictionary<string, string>? Query { get; set; }

    /// <summary>Gets or sets the body template.</summary>
    public string? Body { get; set; }

    /// <summary>Gets or sets the body content type.</summary>
    public string? ContentType { get; set; }
}

/// <summary>Pagination.</summary>
public sealed class PaginationSpec
{
    /// <summary>Gets or sets the type.</summary>
    public PaginationType Type { get; set; }

    /// <summary>Gets or sets the query parameter name.</summary>
    public string? Param { get; set; }

    /// <summary>Gets or sets the first page number.</summary>
    public int? Start { get; set; }

    /// <summary>Gets or sets the page size.</summary>
    public int? Size { get; set; }

    /// <summary>Gets or sets the page size parameter.</summary>
    public string? SizeParam { get; set; }

    /// <summary>Gets or sets the cursor path.</summary>
    public string? CursorPath { get; set; }

    /// <summary>Gets or sets the next URL path.</summary>
    public string? NextUrlPath { get; set; }

    /// <summary>Gets or sets the total pages path.</summary>
    public string? TotalPagesPath { get; set; }

    /// <summary>Gets or sets the maximum pages.</summary>
    public int? MaxPages { get; set; }
}

/// <summary>Date iteration range.</summary>
public sealed class DateRangeSpec
{
    /// <summary>Gets or sets the first day offset (≤ 0).</summary>
    public int? FromDays { get; set; }

    /// <summary>Gets or sets the last day offset.</summary>
    public int? ToDays { get; set; }

    /// <summary>Gets or sets the date format.</summary>
    public string? Format { get; set; }
}

/// <summary>Condition used by skipIf.</summary>
public sealed class ConditionSpec
{
    /// <summary>Gets or sets the mapped field name or JSONPath.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the value that must be equal.</summary>
    [JsonPropertyName("equals")]
    [JsonConverter(typeof(LooseStringConverter))]
    public string? EqualsValue { get; set; }

    /// <summary>Gets or sets the value that must differ.</summary>
    [JsonConverter(typeof(LooseStringConverter))]
    public string? NotEquals { get; set; }

    /// <summary>Gets or sets a regex that must match.</summary>
    public string? Matches { get; set; }

    /// <summary>Gets or sets a value indicating whether the field must be missing.</summary>
    public bool? Missing { get; set; }
}

/// <summary>An endpoint: request + mapping.</summary>
public sealed class EndpointSpec
{
    /// <summary>Gets or sets the id.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the scope.</summary>
    public EndpointScope? Scope { get; set; }

    /// <summary>Gets or sets the request.</summary>
    public RequestSpec Request { get; set; } = new();

    /// <summary>Gets or sets the iteration mode.</summary>
    public ForEachMode? ForEach { get; set; }

    /// <summary>Gets or sets the date range.</summary>
    public DateRangeSpec? DateRange { get; set; }

    /// <summary>Gets or sets the pagination.</summary>
    public PaginationSpec? Pagination { get; set; }

    /// <summary>Gets or sets the refresh interval.</summary>
    public int? RefreshSeconds { get; set; }

    /// <summary>Gets or sets the body format.</summary>
    public BodyFormat? Format { get; set; }

    /// <summary>Gets or sets the items root path.</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets the field mapping.</summary>
    public FieldMap? Fields { get; set; }

    /// <summary>Gets or sets the inline streams.</summary>
    public InlineStreamsSpec? Streams { get; set; }

    /// <summary>Gets or sets skip conditions.</summary>
    public List<ConditionSpec>? SkipIf { get; set; }
}

/// <summary>Participants mapping.</summary>
public sealed class ParticipantsSpec
{
    /// <summary>Gets or sets the path of the participant nodes.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the name field (relative to each participant).</summary>
    public FieldSpec? Name { get; set; }

    /// <summary>Gets or sets the logo field.</summary>
    public FieldSpec? Logo { get; set; }
}

/// <summary>Streams embedded in each item.</summary>
public sealed class InlineStreamsSpec
{
    /// <summary>Gets or sets the stream nodes path (relative to the item).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream fields.</summary>
    public Dictionary<string, FieldSpec> Fields { get; set; } = new();
}

/// <summary>One level of a stream chain.</summary>
public sealed class StreamLevelSpec
{
    /// <summary>Gets or sets the request.</summary>
    public RequestSpec Request { get; set; } = new();

    /// <summary>Gets or sets the root path.</summary>
    public string? Root { get; set; }

    /// <summary>Gets or sets the context name used by the next level ({source.id}).</summary>
    public string? As { get; set; }

    /// <summary>Gets or sets the body format.</summary>
    public BodyFormat? Format { get; set; }

    /// <summary>Gets or sets the fields.</summary>
    public Dictionary<string, FieldSpec>? Fields { get; set; }
}

/// <summary>Stream chain (event → sources → streams).</summary>
public sealed class StreamChainSpec
{
    /// <summary>Gets or sets a value indicating whether streams are fetched on demand.</summary>
    public bool? Lazy { get; set; }

    /// <summary>Gets or sets the cache duration.</summary>
    public int? CacheSeconds { get; set; }

    /// <summary>Gets or sets the levels.</summary>
    public List<StreamLevelSpec> Levels { get; set; } = new();
}

/// <summary>M3U settings.</summary>
public sealed class M3uSpec
{
    /// <summary>Gets or sets the playlist URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the XMLTV URL.</summary>
    public string? XmltvUrl { get; set; }

    /// <summary>Gets or sets the mode.</summary>
    public M3uMode? Mode { get; set; }

    /// <summary>Gets or sets the event title regex.</summary>
    public string? EventTitlePattern { get; set; }

    /// <summary>Gets or sets the include regex on group-title.</summary>
    public string? IncludeGroups { get; set; }

    /// <summary>Gets or sets the exclude regex on group-title.</summary>
    public string? ExcludeGroups { get; set; }

    /// <summary>Gets or sets the refresh interval.</summary>
    public int? RefreshMinutes { get; set; }
}

/// <summary>Xtream Codes settings.</summary>
public sealed class XtreamSpec
{
    /// <summary>Gets or sets the server URL.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name template.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the password template.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the output container.</summary>
    public XtreamOutput? Output { get; set; }

    /// <summary>Gets or sets a value indicating whether the EPG is imported.</summary>
    public bool? IncludeEpg { get; set; }

    /// <summary>Gets or sets the include regex on category names.</summary>
    public string? IncludeCategories { get; set; }

    /// <summary>Gets or sets the exclude regex on category names.</summary>
    public string? ExcludeCategories { get; set; }

    /// <summary>Gets or sets the refresh interval.</summary>
    public int? RefreshMinutes { get; set; }
}

/// <summary>Direct stream settings.</summary>
public sealed class DirectSpec
{
    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the logo.</summary>
    public string? Logo { get; set; }

    /// <summary>Gets or sets the category.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the type.</summary>
    public StreamTypeSpec? Type { get; set; }

    /// <summary>Gets or sets the required headers.</summary>
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>Resolver settings.</summary>
public sealed class ResolverSpec
{
    /// <summary>Gets or sets a value indicating whether the resolver runs.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets the strategy.</summary>
    public ResolverStrategy? Strategy { get; set; }

    /// <summary>Gets or sets the referer used to fetch pages.</summary>
    public string? Referer { get; set; }

    /// <summary>Gets or sets the iframe depth.</summary>
    public int? MaxDepth { get; set; }

    /// <summary>Gets or sets the cache duration.</summary>
    public int? CacheSeconds { get; set; }
}

/// <summary>Refresh settings.</summary>
public sealed class RefreshSpec
{
    /// <summary>Gets or sets the live interval.</summary>
    public int? LiveSeconds { get; set; }

    /// <summary>Gets or sets the today interval.</summary>
    public int? TodaySeconds { get; set; }

    /// <summary>Gets or sets the full calendar interval.</summary>
    public int? AllSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether polling adapts to what is on air.</summary>
    public bool? Adaptive { get; set; }
}

/// <summary>Category/region rule.</summary>
public sealed class CategoryRuleSpec
{
    /// <summary>Gets or sets the regex.</summary>
    public string Match { get; set; } = string.Empty;

    /// <summary>Gets or sets the category to assign.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the country to assign.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the language to assign.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the duration to assign.</summary>
    public int? DurationMinutes { get; set; }
}

/// <summary>OpenAPI metadata.</summary>
public sealed class OpenApiSpec
{
    /// <summary>Gets or sets the spec URL.</summary>
    public string? SpecUrl { get; set; }

    /// <summary>Gets or sets the selected operations.</summary>
    public List<string>? SelectedOperations { get; set; }
}
