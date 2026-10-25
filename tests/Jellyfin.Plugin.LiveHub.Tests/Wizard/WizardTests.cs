using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Jellyfin.Plugin.LiveHub.Wizard;
using LiveHub.MockServer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Wizard;

public sealed class WizardTests : IClassFixture<MockServerFixture>, IDisposable
{
    private readonly MockServerFixture _mock;
    private readonly string _dir;
    private readonly WizardService _wizard;
    private readonly DefinitionStore _store;

    public WizardTests(MockServerFixture mock)
    {
        _mock = mock;
        _dir = Path.Combine(Path.GetTempPath(), "livehub-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new LiveHubPaths(Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"));
        _store = new DefinitionStore(paths, new SecretProtector(paths), NullLogger<DefinitionStore>.Instance);
        _wizard = new WizardService(mock.CreateEngine(), mock.CreateHttp(), _store, NullLogger<WizardService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private static WizardInput Input(string path) => new() { Url = MockServerFixture.BaseUrl + path, AllowPrivateNetwork = true };

    private async Task<WizardPreview> PreviewAsync(ProviderDefinition def, Dictionary<string, string>? secrets = null)
    {
        // The UI keeps the secrets typed in the wizard and sends them back with the definition.
        if (secrets is not null)
        {
            def.Secrets = new Dictionary<string, string>(secrets);
        }

        return await _wizard.PreviewAsync(def, null, CancellationToken.None);
    }

    [Fact]
    public async Task Api1_FlatArrayIsMappedAutomatically()
    {
        var d = await _wizard.DetectAsync(Input("/api1/events.json"), CancellationToken.None);
        Assert.Equal(SourceFormat.RestJson, d.Format);
        var ep = Assert.Single(d.Definition!.Events!);
        Assert.Equal("$[*]", ep.Root);
        Assert.Equal("$.title", ep.Fields!["title"].Path);
        Assert.Equal("$.id", ep.Fields["id"].Path);
        Assert.Equal("$.start", ep.Fields["start"].Path);
        Assert.Equal("unix", ep.Fields["start"].Format);
        Assert.Equal("$.sport", ep.Fields["category"].Path);
        Assert.Equal("$.league", ep.Fields["competition"].Path);
        Assert.Equal("$.streams[*]", ep.Streams!.Path);
        Assert.Equal("$.url", ep.Streams.Fields["url"].Path);
        Assert.Equal("$.q", ep.Streams.Fields["quality"].Path);
        Assert.Equal("$.lang", ep.Streams.Fields["language"].Path);

        var p = await PreviewAsync(d.Definition);
        Assert.True(p.Valid, string.Join("; ", p.ValidationErrors));
        Assert.Empty(p.Errors);
        Assert.Equal(4, p.Counts["events"]);
        Assert.True(p.Counts["streams"] >= 5);
        Assert.Contains(p.Rows, r => r.Title == "Inter - Milan" && r.StreamCount == 2);
    }

    [Fact]
    public async Task Api2_NestedCursorApiKeyIsMappedAutomatically()
    {
        var secrets = new Dictionary<string, string> { ["apiKey"] = MockServerApp.Api2Key };
        var input = Input("/api2/v2/schedule?limit=2");
        input.Auth = new AuthSpec { Type = AuthType.ApiKey, In = KeyLocation.Header, Name = "X-Api-Key", Value = "{secret.apiKey}" };
        input.Secrets = secrets;
        var d = await _wizard.DetectAsync(input, CancellationToken.None);
        Assert.Equal(SourceFormat.RestJson, d.Format);
        var ep = Assert.Single(d.Definition!.Events!);
        Assert.Equal("$.data.items[*]", ep.Root);
        Assert.Equal("$.name.display", ep.Fields!["title"].Path);
        Assert.Equal("$.competition.title", ep.Fields["competition"].Path);
        Assert.Equal("$.timing.startsAt", ep.Fields["start"].Path);
        Assert.Equal("iso8601", ep.Fields["start"].Format);
        Assert.Equal("$.timing.endsAt", ep.Fields["end"].Path);
        Assert.Equal("$.uid", ep.Fields["id"].Path);
        Assert.Equal("$.state", ep.Fields["status"].Path);
        Assert.Equal("$.teams[*]", ep.Fields.Participants!.Path);
        Assert.Equal("$.feeds[*]", ep.Streams!.Path);
        Assert.Equal("$.src", ep.Streams.Fields["url"].Path);
        Assert.Equal(PaginationType.Cursor, ep.Pagination!.Type);
        Assert.Equal("$.meta.next_cursor", ep.Pagination.CursorPath);
        Assert.DoesNotContain("limit=2", ep.Request.Url, StringComparison.Ordinal);
        Assert.Equal(SecretProtector.Mask, d.Definition.Secrets!["apiKey"]);

        ep.Pagination.SizeParam = "limit";
        ep.Pagination.Size = 2;
        var p = await PreviewAsync(d.Definition, secrets);
        Assert.True(p.Valid, string.Join("; ", p.ValidationErrors));
        Assert.Empty(p.Errors);
        Assert.Equal(5, p.Counts["events"]);
    }

    [Fact]
    public async Task Api3_HomeAwayAndCustomDatePatternAreDetected_StreamChainAddedByAdmin()
    {
        var input = Input("/api3/matches");
        input.SampleResponse = """{"matches":[{"matchId":"m1","home":"Juventus","away":"Napoli","kickoff":"25/10/2026 18:00","competition":"Serie A","sport":"football"},{"matchId":"m2","home":"Atalanta","away":"Torino","kickoff":"25/10/2026 20:45","competition":"Serie A","sport":"football"}]}""";
        var d = await _wizard.DetectAsync(input, CancellationToken.None);
        var ep = Assert.Single(d.Definition!.Events!);
        Assert.Equal("$.matches[*]", ep.Root);
        Assert.Equal("dd/MM/yyyy HH:mm", ep.Fields!["start"].Format);
        Assert.Equal("$.home", ep.Fields["homeTeam"].Path);
        Assert.Equal("$.away", ep.Fields["awayTeam"].Path);
        Assert.Contains("{$.home} vs {$.away}", ep.Fields["title"].Template, StringComparison.Ordinal);
        Assert.Contains(d.Warnings, w => w.Contains("stream", StringComparison.OrdinalIgnoreCase));

        // What the admin adds in the form: OAuth2, time zone and the two-level stream chain.
        var def = d.Definition;
        def.Defaults!.Timezone = "Europe/Rome";
        def.Connection!.Auth = new AuthSpec { Type = AuthType.Oauth2ClientCredentials, TokenUrl = "{baseUrl}/api3/oauth/token", ClientId = "livehub", ClientSecret = "{secret.cs}" };
        def.Streams = Fixtures.Definition("mock3-multilevel.json").Streams;
        foreach (var level in def.Streams!.Levels)
        {
            level.Request.Url = level.Request.Url.Replace("{baseUrl}", "{baseUrl}/api3", StringComparison.Ordinal);
        }

        var p = await PreviewAsync(def, new Dictionary<string, string> { ["cs"] = MockServerApp.Api3ClientSecret });
        Assert.True(p.Valid, string.Join("; ", p.ValidationErrors));
        Assert.Empty(p.Errors);
        Assert.Equal(2, p.Counts["events"]);
        Assert.Contains(p.Rows, r => r.StreamCount == 4);
    }

    [Fact]
    public async Task Api4_EmbedUrlsBecomeEmbedStreams()
    {
        var d = await _wizard.DetectAsync(Input("/api4/feed.json"), CancellationToken.None);
        var ep = Assert.Single(d.Definition!.Events!);
        Assert.Equal("$.events[*]", ep.Root);
        Assert.Equal("$", ep.Streams!.Path);
        Assert.Equal("$.embed", ep.Streams.Fields["url"].Path);
        Assert.Equal("embed", ep.Streams.Fields["type"].Value);
        Assert.Equal("yyyy-MM-dd HH:mm:ss", ep.Fields!["start"].Format);
        var p = await PreviewAsync(d.Definition);
        Assert.Equal(2, p.Counts["events"]);
        Assert.All(p.Rows, r => Assert.Equal("Embed", r.Streams.Single().Kind));
    }

    [Fact]
    public async Task Api5_M3uPlaylistIsRecognised()
    {
        var d = await _wizard.DetectAsync(Input("/m3u/playlist.m3u"), CancellationToken.None);
        Assert.Equal(SourceFormat.M3u, d.Format);
        Assert.Equal(ProviderKind.M3u, d.Definition!.Kind);
        Assert.Equal(MockServerFixture.BaseUrl + "/xmltv/guide.xml.gz", d.Definition.M3u!.XmltvUrl);
        var p = await PreviewAsync(d.Definition);
        Assert.Equal(3, p.Counts["channels"]);
        Assert.True(p.Counts["programs"] > 0);
    }

    [Fact]
    public async Task Api6_XtreamIsRecognisedAndCredentialsBecomeSecrets()
    {
        var d = await _wizard.DetectAsync(Input("/xtream/player_api.php?username=user&password=pass"), CancellationToken.None);
        Assert.Equal(SourceFormat.Xtream, d.Format);
        Assert.Equal(MockServerFixture.BaseUrl + "/xtream", d.Definition!.Xtream!.Server);
        Assert.Equal("{secret.password}", d.Definition.Xtream.Password);
        Assert.Equal(SecretProtector.Mask, d.Definition.Secrets!["password"]);
        var p = await PreviewAsync(d.Definition, new Dictionary<string, string> { ["username"] = "user", ["password"] = "pass" });
        Assert.Equal(3, p.Counts["channels"]);
    }

    [Fact]
    public async Task OpenApiListsOperationsAndMapsTheChosenOne()
    {
        var input = Input("/api2/openapi.json");
        var d = await _wizard.DetectAsync(input, CancellationToken.None);
        Assert.Equal(SourceFormat.OpenApi, d.Format);
        Assert.Contains(d.Operations, o => o.Key == "GET /v2/schedule" && o.SuggestedRole == "events");
        Assert.Contains(d.Operations, o => o.Key == "GET /v2/categories" && o.SuggestedRole == "categories");

        input.OpenApiOperation = "GET /v2/schedule";
        input.Secrets = new Dictionary<string, string> { ["apiKey"] = MockServerApp.Api2Key };
        var chosen = await _wizard.DetectAsync(input, CancellationToken.None);
        Assert.NotNull(chosen.Definition);
        Assert.Equal(AuthType.ApiKey, chosen.Definition!.Connection!.Auth!.Type);
        Assert.Equal("X-Api-Key", chosen.Definition.Connection.Auth.Name);
        Assert.Equal(MockServerFixture.BaseUrl + "/api2", chosen.Definition.Connection.BaseUrl);
        Assert.Equal("{baseUrl}/v2/schedule", chosen.Definition.Events!.Single().Request.Url);
    }

    [Fact]
    public async Task HlsAndWebPagesAreRecognised()
    {
        var hls = await _wizard.DetectAsync(Input("/hls/demo/master.m3u8"), CancellationToken.None);
        Assert.Equal(SourceFormat.DirectStream, hls.Format);
        Assert.Equal(StreamTypeSpec.Hls, hls.Definition!.Direct!.Type);

        var page = await _wizard.DetectAsync(Input("/embed/e1"), CancellationToken.None);
        Assert.Equal(SourceFormat.WebPage, page.Format);
        Assert.Equal(StreamTypeSpec.Embed, page.Definition!.Direct!.Type);
    }

    [Fact]
    public async Task ProbeErrorsAreReportedWithoutLeakingSecrets()
    {
        var input = Input("/api2/v2/schedule");
        input.Auth = new AuthSpec { Type = AuthType.ApiKey, In = KeyLocation.Query, Name = "key", Value = "{secret.k}" };
        input.Secrets = new Dictionary<string, string> { ["k"] = "very-secret-value" };
        var d = await _wizard.DetectAsync(input, CancellationToken.None);
        Assert.Equal(401, d.HttpStatus);
        Assert.DoesNotContain(d.Warnings, w => w.Contains("very-secret-value", StringComparison.Ordinal));

        var blocked = await _wizard.DetectAsync(new WizardInput { Url = MockServerFixture.BaseUrl + "/api1/events.json" }, CancellationToken.None);
        Assert.Contains(blocked.Warnings, w => w.Contains("local", StringComparison.OrdinalIgnoreCase) || w.Contains("private", StringComparison.OrdinalIgnoreCase));
    }
}
