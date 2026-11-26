using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RichardSzalay.MockHttp;

namespace Nordray.WhiteRabbit.Tests.Integration;

/// <summary>
/// Bunny Sites through White Rabbit: site-scoped bindings, pull request bindings, the storage proxy that injects
/// the zone password, and the redaction of zone secrets. The fake account has a production site (example-site), its PR 7
/// preview, another site and a storage zone that is not a site.
/// </summary>
public class SitesTests : CliFederationTests
{
    private const string ProdZone = "sites-example-site-aaaaaa";
    private const string PreviewZone = "sites-example-site-pr-7-bbbbbb";
    private const string OtherZone = "sites-other-cccccc";
    private const string PrivateZone = "private-storage";
    private const string StorageHost = "se.storage.bunnycdn.com";

    private static readonly (int Id, string Name, int StorageZoneId)[] PullZones =
        [(10, ProdZone, 100), (11, PreviewZone, 101), (13, OtherZone, 103), (14, "private-pullzone", 0)];

    private static readonly (int Id, string Name, string Password)[] StorageZones =
        [(100, ProdZone, "pw-prod-zone"), (101, PreviewZone, "pw-preview-zone"), (103, OtherZone, "pw-other-zone"), (104, PrivateZone, "pw-private-zone")];

    private readonly List<(HttpRequestMessage Request, byte[] Body)> _storageRequests = [];

    private static JsonObject PullZone((int Id, string Name, int StorageZoneId) z) => new()
    {
        ["Id"] = z.Id,
        ["Name"] = z.Name,
        ["StorageZoneId"] = z.StorageZoneId,
        ["ZoneSecurityKey"] = "token-auth-secret",
        ["Hostnames"] = new JsonArray(new JsonObject { ["Value"] = z.Name + ".b-cdn.net", ["CertificateKey"] = "PRIVATE-KEY-PEM" }),
    };

    private static JsonObject StorageZone((int Id, string Name, string Password) z) => new()
    {
        ["Id"] = z.Id,
        ["Name"] = z.Name,
        ["Region"] = "SE",
        ["Password"] = z.Password,
        ["ReadOnlyPassword"] = "ro-" + z.Password,
    };

    private void FakeAccount()
    {
        _upstream = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var segments = path.Trim('/').Split('/');
            JsonNode? body = (request.Method.Method, segments) switch
            {
                ("GET", ["pullzone"]) => new JsonArray(PullZones.Select(z => (JsonNode)PullZone(z)).ToArray()),
                ("GET", ["pullzone", var id]) => PullZones.Where(z => z.Id.ToString() == id).Select(z => (JsonNode)PullZone(z)).FirstOrDefault(),
                ("GET", ["storagezone"]) => new JsonArray(StorageZones
                    .Where(z => !request.RequestUri.Query.Contains("search=") || request.RequestUri.Query.Contains(z.Name))
                    .Select(z => (JsonNode)StorageZone(z)).ToArray()),
                ("GET", ["storagezone", var id]) => StorageZones.Where(z => z.Id.ToString() == id).Select(z => (JsonNode)StorageZone(z)).FirstOrDefault(),
                _ => new JsonObject { ["ok"] = true },
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        };
        _factory.Upstream.When($"https://{StorageHost}/*").Respond(request =>
        {
            var bytes = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? [];
            lock (_storageRequests) _storageRequests.Add((request, bytes));
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"HttpCode":201}""") };
        });
    }

    private BindingBody PullRequestBinding(params string[] capabilities) => new(
        "codebahn", "acme/example-site", null, "", "acme/example-site/.github/workflows/deploy-preview.yml",
        Events: ["pull_request"], Capabilities: capabilities.Length > 0 ? capabilities : ["sites.read", "sites.deploy", "sites.manage"],
        SitePrefix: "example-site-pr-");

    private static string PullRequestToken(int number = 7, string workflow = "deploy-preview.yml")
    {
        var claims = IdentityTokens.ForgejoClaims(repository: "acme/example-site", @ref: $"refs/pull/{number}/head", eventName: "pull_request");
        claims["sub"] = "repo:acme/example-site:pull_request";
        claims["workflow_ref"] = $"acme/example-site/.github/workflows/{workflow}@refs/pull/{number}/head";
        return ForgejoToken(claims);
    }

    /// <summary>A pull request binding, a PR 7 job's session and a key with all three sites.* capabilities.</summary>
    private async Task<(string Session, string Key, JsonElement Principal)> PreviewKeyAsync()
    {
        FakeAccount();
        var human = await HumanSessionAsync();
        var (created, problems) = await CreateBindingAsync(human, PullRequestBinding());
        Assert.True(created == HttpStatusCode.Created, problems.ToString());
        var (status, body) = await ExchangeAsync(PullRequestToken());
        Assert.Equal(HttpStatusCode.OK, status);
        var session = body.GetProperty("access_token").GetString()!;
        return (session, await CredentialAsync(session), body.GetProperty("principal"));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key, string? json = null, byte[]? raw = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("AccessKey", key);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (raw is not null) request.Content = new ByteArrayContent(raw);
        return _client.SendAsync(request);
    }

    private static string StoragePath(string zone, string path) => $"/proxy/storage/{StorageHost}/{zone}/{path}";

    [Fact]
    public async Task PullRequestJob_ReachesExactlyItsOwnPreviewSite()
    {
        var (_, key, principal) = await PreviewKeyAsync();
        Assert.Equal("example-site-pr-7", principal.GetProperty("sites")[0].GetString());
        Assert.Equal(1, principal.GetProperty("sites").GetArrayLength());

        // Lists show only the preview's zones, without their secrets.
        var pullZones = await (await SendAsync(HttpMethod.Get, "/proxy/api.bunny.net/pullzone", key)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(PreviewZone, pullZones);
        Assert.DoesNotContain(ProdZone, pullZones);
        Assert.DoesNotContain(OtherZone, pullZones);
        Assert.DoesNotContain("private-pullzone", pullZones);
        Assert.DoesNotContain("token-auth-secret", pullZones);
        Assert.DoesNotContain("PRIVATE-KEY-PEM", pullZones);

        // Production and other zones are "not found", by ID too.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/proxy/api.bunny.net/pullzone/10", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/proxy/api.bunny.net/storagezone/100", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone/10/purgeCache", key, "{}")).StatusCode);

        var zone = await SendAsync(HttpMethod.Get, "/proxy/api.bunny.net/storagezone/101", key);
        Assert.Equal(HttpStatusCode.OK, zone.StatusCode);
        var zoneJson = await zone.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("pw-preview-zone", zoneJson);
        Assert.Contains("[redacted by White Rabbit]", zoneJson);
    }

    [Fact]
    public async Task StorageUpload_GetsTheZonesOwnPassword_OnTheZonesRegionHost()
    {
        var (_, key, _) = await PreviewKeyAsync();
        var file = Encoding.UTF8.GetBytes("<h1>preview</h1>");

        // The caller sends the placeholder it was shown as the zone password; rabbit replaces it with the key anyway.
        var response = await SendAsync(HttpMethod.Put, StoragePath(PreviewZone, "deploys/d1/index%20page.html"), key, raw: file);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var (forwarded, body) = Assert.Single(_storageRequests);
        Assert.Equal($"https://{StorageHost}/{PreviewZone}/deploys/d1/index%20page.html", forwarded.RequestUri!.AbsoluteUri);
        Assert.Equal("pw-preview-zone", forwarded.Headers.GetValues("AccessKey").Single());
        Assert.Equal(file, body);
        await AssertNoSecretsInAuditAsync("pw-preview-zone", RealBunnyKey, key);

        // A directory listing keeps its trailing slash.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Get, StoragePath(PreviewZone, "deploys/"), key)).StatusCode);
        Assert.EndsWith("/deploys/", _storageRequests[^1].Request.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(ProdZone)]
    [InlineData(OtherZone)]
    [InlineData(PrivateZone)]
    public async Task StorageOutsideTheScope_IsNotFound_AndNeverForwarded(string zoneName)
    {
        var (_, key, _) = await PreviewKeyAsync();
        var response = await SendAsync(HttpMethod.Put, StoragePath(zoneName, "_bunny/site.json"), key, raw: "{}"u8.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_storageRequests);
    }

    [Theory]
    [InlineData($"{PreviewZone}/../{ProdZone}/index.html")]
    [InlineData($"{PreviewZone}/%2e%2e/{ProdZone}/index.html")]
    [InlineData($"{PreviewZone}/%2E%2E/{ProdZone}/index.html")]
    [InlineData($"{PreviewZone}/%252e%252e/{ProdZone}/index.html")]
    [InlineData($"{PreviewZone}/a%2F..%2F..%2F{ProdZone}/index.html")]
    [InlineData($"{PreviewZone}/./index.html")]
    [InlineData($"{PreviewZone}/a%5C..%5C{ProdZone}")]
    public void StorageUri_StaysInsideTheCheckedZone(string zonePath) =>
        Assert.Null(Nordray.WhiteRabbit.Web.Proxy.ScopePolicy.StorageUri($"/proxy/storage/{StorageHost}/{zonePath}", "", StorageHost, PreviewZone));

    [Fact]
    public void StorageUri_KeepsEncodingAndTrailingSlash()
    {
        Assert.Equal($"https://{StorageHost}/{PreviewZone}/deploys/a%20b.html?x=1",
            Nordray.WhiteRabbit.Web.Proxy.ScopePolicy.StorageUri($"/proxy/storage/{StorageHost}/{PreviewZone}/deploys/a%20b.html", "?x=1", StorageHost, PreviewZone)!.AbsoluteUri);
        Assert.Equal($"https://{StorageHost}/{PreviewZone}/",
            Nordray.WhiteRabbit.Web.Proxy.ScopePolicy.StorageUri($"/proxy/storage/{StorageHost}/{PreviewZone}/", "", StorageHost, PreviewZone)!.AbsoluteUri);
    }

    [Theory]
    [InlineData("%2e%2e")]
    [InlineData("%2E%2E")]
    [InlineData("%252e%252e")]
    public async Task StorageTraversal_IsRefused_AndNeverForwarded(string dots)
    {
        var (_, key, _) = await PreviewKeyAsync();
        var response = await SendAsync(HttpMethod.Put, $"/proxy/storage/{StorageHost}/{PreviewZone}/{dots}/{ProdZone}/index.html", key, raw: [1]);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, response.StatusCode.ToString());
        Assert.Empty(_storageRequests);
    }

    [Fact]
    public async Task SiteDeploy_CanDetachButNeverAttachAnEdgeScript()
    {
        var (_, key, _) = await PreviewKeyAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone/11", key, """{"MiddlewareScriptId":0}""")).StatusCode);
        foreach (var value in new[] { "5", "\"0\"", "null" })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone/11", key, $$"""{"MiddlewareScriptId":{{value}}}""")).StatusCode);
    }

    [Fact]
    public async Task StorageZoneUpdate_AnswerIsRedacted()
    {
        var (_, key, _) = await PreviewKeyAsync();
        var inner = _upstream;
        _upstream = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/storagezone/101"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StorageZone(StorageZones[1]).ToJsonString()) }
            : inner(request);
        var answer = await (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/storagezone/101", key, """{"Custom404FilePath":"/deploys/d1/404.html"}""")).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("pw-preview-zone", answer);
    }

    [Fact]
    public async Task StorageOnAnUnknownHost_IsNotFound()
    {
        var (_, key, _) = await PreviewKeyAsync();
        var response = await SendAsync(HttpMethod.Get, $"/proxy/storage/evil.example.com/{PreviewZone}/", key);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_storageRequests);
    }

    [Fact]
    public async Task PreviewCreate_OnlyItsOwnZones_WithItsOwnStorage()
    {
        var (_, key, _) = await PreviewKeyAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/storagezone", key,
            """{"Name":"sites-example-site-pr-7-dddddd","Region":"SE"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/storagezone", key,
            """{"Name":"sites-example-site-dddddd","Region":"SE"}""")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone", key,
            """{"Name":"sites-example-site-pr-7-eeeeee","StorageZoneId":101,"OriginType":2}""")).StatusCode);
        // A preview pull zone that would serve production's storage, or an origin other than storage.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone", key,
            """{"Name":"sites-example-site-pr-7-eeeeee","StorageZoneId":100,"OriginType":2}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone", key,
            """{"Name":"sites-example-site-pr-7-eeeeee","StorageZoneId":101,"OriginType":0}""")).StatusCode);
        // Without OriginType, Bunny would pick its default origin, not the site's storage.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone", key,
            """{"Name":"sites-example-site-pr-7-eeeeee","StorageZoneId":101}""")).StatusCode);
        // A field `bunny sites create` does not send.
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone", key,
            """{"Name":"sites-example-site-pr-7-eeeeee","StorageZoneId":101,"OriginUrl":"https://example.com"}""")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/11", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/storagezone/101", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/storagezone/100", key)).StatusCode);
    }

    [Fact]
    public async Task SiteGrantedPullZoneUpdate_AcceptsOnlyTheSitesCacheSettings()
    {
        var (_, key, _) = await PreviewKeyAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone/11", key,
            """{"CacheControlMaxAgeOverride":2592000,"CacheControlPublicMaxAgeOverride":0}""")).StatusCode);
        var refused = await SendAsync(HttpMethod.Post, "/proxy/api.bunny.net/pullzone/11", key, """{"OriginUrl":"https://example.com"}""");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("body_field_not_allowed", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnotherPullRequestsJob_GetsItsOwnPreviewSite()
    {
        FakeAccount();
        var human = await HumanSessionAsync();
        await CreateBindingAsync(human, PullRequestBinding());
        var (_, body) = await ExchangeAsync(PullRequestToken(number: 8));
        var key = await CredentialAsync(body.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, StoragePath(PreviewZone, ""), key)).StatusCode);
    }

    [Fact]
    public async Task PullRequestFromAnotherWorkflow_IsDenied()
    {
        FakeAccount();
        await CreateBindingAsync(await HumanSessionAsync(), PullRequestBinding());
        await AssertExchangeDenied(PullRequestToken(workflow: "deploy-production.yml"), "event_not_allowed");
    }

    [Fact]
    public async Task ProductionBinding_ReachesItsSite_ButNotThePreviews()
    {
        FakeAccount();
        var human = await HumanSessionAsync();
        var binding = new BindingBody("codebahn", "acme/example-site", null, "", "acme/example-site/.github/workflows/deploy-production.yml",
            Capabilities: ["sites.read", "sites.deploy"], Sites: ["example-site"]);
        var (created, problems) = await CreateBindingAsync(human, binding);
        Assert.True(created == HttpStatusCode.Created, problems.ToString());

        var claims = IdentityTokens.ForgejoClaims(repository: "acme/example-site", @ref: "refs/tags/v1.2.0");
        claims["workflow_ref"] = "acme/example-site/.github/workflows/deploy-production.yml@refs/tags/v1.2.0";
        var (_, body) = await ExchangeAsync(ForgejoToken(claims));
        var key = await CredentialAsync(body.GetProperty("access_token").GetString()!);

        Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Put, StoragePath(ProdZone, "deploys/v1/index.html"), key, raw: [1])).StatusCode);
        Assert.Equal("pw-prod-zone", _storageRequests.Single().Request.Headers.GetValues("AccessKey").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, StoragePath(PreviewZone, "x"), key, raw: [1])).StatusCode);
        // sites.deploy cannot create or delete zones.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/10", key)).StatusCode);
    }

    [Fact]
    public async Task Human_SeesEverySiteZone_ButNoOtherStorage_AndNoPasswords()
    {
        FakeAccount();
        var key = await CredentialAsync(await HumanSessionAsync(), "sites.read");
        var zones = await (await SendAsync(HttpMethod.Get, "/proxy/api.bunny.net/storagezone", key)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(ProdZone, zones);
        Assert.Contains(OtherZone, zones);
        Assert.DoesNotContain(PrivateZone, zones);
        Assert.DoesNotContain("pw-", zones);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, StoragePath(PrivateZone, ""), key)).StatusCode);
    }

    [Fact]
    public async Task PullZoneRead_NeverShowsPullZoneSecrets()
    {
        FakeAccount();
        var key = await CredentialAsync(await HumanSessionAsync(), "pullzone.read");
        foreach (var path in new[] { "/proxy/api.bunny.net/pullzone", "/proxy/api.bunny.net/pullzone/14" })
        {
            var json = await (await SendAsync(HttpMethod.Get, path, key)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("token-auth-secret", json);
            Assert.DoesNotContain("PRIVATE-KEY-PEM", json);
            Assert.Contains("private-pullzone", json); // pullzone.read is not limited to sites
        }
    }

    [Theory]
    [InlineData("mc_deploy_on_pull_request_binding")]
    [InlineData("push_on_pull_request_binding")]
    [InlineData("sites_without_site")]
    [InlineData("pull_request_without_prefix")]
    public async Task Admin_SiteBindingsStayNarrow(string variant)
    {
        var human = await HumanSessionAsync();
        var binding = variant switch
        {
            "mc_deploy_on_pull_request_binding" => PullRequestBinding("sites.deploy", "mc.deploy"),
            "push_on_pull_request_binding" => PullRequestBinding() with { Events = ["pull_request", "push"] },
            "sites_without_site" => PullRequestBinding() with { SitePrefix = null, Events = ["push"] },
            "pull_request_without_prefix" => PullRequestBinding() with { SitePrefix = null, Sites = ["example-site"] },
            _ => throw new ArgumentException(variant),
        };
        var (status, body) = await CreateBindingAsync(human, binding);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_binding", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Bindings_ListShowsTheirSites()
    {
        var human = await HumanSessionAsync();
        await CreateBindingAsync(human, PullRequestBinding());
        var listed = (await GetJsonAsync("/api/admin/bindings", human))[0];
        Assert.Equal("example-site-pr-", listed.GetProperty("site_prefix").GetString());
        Assert.Equal(0, listed.GetProperty("sites").GetArrayLength());
    }
}
