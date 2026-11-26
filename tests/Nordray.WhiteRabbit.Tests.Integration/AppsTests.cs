using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nordray.WhiteRabbit.Tests.Integration;

/// <summary>
/// Magic Containers apps scoped by name: bindings with <c>--app</c>, pull request bindings with <c>--app-prefix</c> (one
/// preview app per PR, created, replaced and deleted with its CDN pull zone), and bindings without apps, which keep the
/// account-wide mc.* capabilities. The fake account has a production app, the PR 7 app and another repository's app.
/// </summary>
public class AppsTests : CliFederationTests
{
    private const string Repo = "acme/example-app";
    private const string Workflow = "acme/example-app/.github/workflows/deploy.yml";

    private static readonly (string Id, string Name)[] Apps =
        [("app-prod", "example-app-master"), ("app-pr7", "example-app-pr-7"), ("app-other", "other-app")];

    // CDN pull zones Bunny made for the apps' endpoints, and one that belongs to no app.
    private static readonly (int Id, string Name, string? AppId)[] PullZones =
        [(20, "mc-aaaaaaaaaa", "app-pr7"), (21, "mc-bbbbbbbbbb", "app-prod"), (22, "plain-pullzone", null)];

    private void FakeAccount()
    {
        _upstream = request =>
        {
            var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            JsonNode? body = (request.Method.Method, segments) switch
            {
                ("GET", ["mc", "apps"]) => new JsonObject { ["items"] = new JsonArray(Apps.Select(a => (JsonNode)App(a)).ToArray()) },
                (_, ["mc", "apps", var id, ..]) => Apps.Where(a => a.Id == id).Select(a => (JsonNode)App(a)).FirstOrDefault(),
                ("GET", ["pullzone", var id]) => PullZones.Where(z => z.Id.ToString() == id).Select(z => (JsonNode)new JsonObject
                {
                    ["Id"] = z.Id,
                    ["Name"] = z.Name,
                    ["MagicContainersAppId"] = z.AppId,
                }).FirstOrDefault(),
                _ => new JsonObject { ["ok"] = true },
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        };
    }

    private static JsonObject App((string Id, string Name) a) => new()
    {
        ["id"] = a.Id,
        ["name"] = a.Name,
        ["containerTemplates"] = new JsonArray(new JsonObject { ["id"] = "c-web", ["name"] = "web", ["environmentVariables"] = new JsonArray() }),
    };

    private static BindingBody PullRequestBinding(params string[] capabilities) => new(
        "codebahn", Repo, null, "", Workflow, Events: ["pull_request"],
        Capabilities: capabilities.Length > 0 ? capabilities : ["mc.read", "mc.manage"], AppPrefix: "example-app-pr-");

    private static string PullRequestToken(int number = 7)
    {
        var claims = IdentityTokens.ForgejoClaims(repository: Repo, @ref: $"refs/pull/{number}/head", eventName: "pull_request");
        claims["sub"] = $"repo:{Repo}:pull_request";
        claims["workflow_ref"] = $"{Workflow}@refs/pull/{number}/head";
        return ForgejoToken(claims);
    }

    private static string TagToken()
    {
        var claims = IdentityTokens.ForgejoClaims(repository: Repo, @ref: "refs/tags/v2.0.0");
        claims["workflow_ref"] = $"{Workflow}@refs/tags/v2.0.0";
        return ForgejoToken(claims);
    }

    private async Task<(string Key, JsonElement Principal)> KeyAsync(BindingBody binding, string token)
    {
        FakeAccount();
        var human = await HumanSessionAsync();
        var (created, problems) = await CreateBindingAsync(human, binding);
        Assert.True(created == HttpStatusCode.Created, problems.ToString());
        var (status, body) = await ExchangeAsync(token);
        Assert.Equal(HttpStatusCode.OK, status);
        return (await CredentialAsync(body.GetProperty("access_token").GetString()!), body.GetProperty("principal"));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("AccessKey", key);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _client.SendAsync(request);
    }

    private const string Mc = "/proxy/api.bunny.net/mc/apps";

    [Fact]
    public async Task PullRequestJob_ReachesExactlyItsOwnApp()
    {
        var (key, principal) = await KeyAsync(PullRequestBinding(), PullRequestToken());
        Assert.Equal(["example-app-pr-7"], principal.GetProperty("apps").EnumerateArray().Select(a => a.GetString()));

        var list = await (await SendAsync(HttpMethod.Get, Mc, key)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("example-app-pr-7", list);
        Assert.DoesNotContain("example-app-master", list);
        Assert.DoesNotContain("other-app", list);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Mc}/app-pr7", key)).StatusCode);
        foreach (var other in new[] { "app-prod", "app-other", "app-missing" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Mc}/{other}", key)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Put, $"{Mc}/{other}", key, """{"name":"example-app-pr-7"}""")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"{Mc}/{other}", key)).StatusCode);
        }
        // Not app-specific: the registries to pull the image from.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/proxy/api.bunny.net/mc/registries", key)).StatusCode);
    }

    [Fact]
    public async Task PullRequestJob_CreatesReplacesAndDeletesOnlyItsApp()
    {
        var (key, _) = await KeyAsync(PullRequestBinding(), PullRequestToken());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, Mc, key, """{"name":"example-app-pr-7"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, Mc, key, """{"name":"example-app-master"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, Mc, key, """{"runtimeType":"Shared"}""")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"{Mc}/app-pr7", key, """{"id":"app-pr7","name":"example-app-pr-7"}""")).StatusCode);
        // A replace cannot rename the app out of the scope.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Put, $"{Mc}/app-pr7", key, """{"name":"example-app-master"}""")).StatusCode);

        // Its CDN pull zone first (while the app still exists), then the app.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/20", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/21", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/22", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, $"{Mc}/app-pr7", key)).StatusCode);

        var forwarded = _upstreamRequests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.RequestUri!.AbsolutePath}").ToList();
        Assert.Equal(["POST /mc/apps", "PUT /mc/apps/app-pr7", "DELETE /pullzone/20", "DELETE /mc/apps/app-pr7"], forwarded);
    }

    [Fact]
    public async Task AnotherPullRequestsJob_CannotTouchThisApp()
    {
        var (key, _) = await KeyAsync(PullRequestBinding(), PullRequestToken(number: 8));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Mc}/app-pr7", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/20", key)).StatusCode);
    }

    [Fact]
    public async Task ProductionBinding_WithAnApp_ReachesOnlyThatApp()
    {
        var binding = new BindingBody("codebahn", Repo, null, "", Workflow, Capabilities: ["mc.read", "mc.manage"], Apps: ["example-app-master"]);
        var (key, _) = await KeyAsync(binding, TagToken());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"{Mc}/app-prod", key, """{"name":"example-app-master"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Mc}/app-pr7", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/21", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/20", key)).StatusCode);
    }

    [Fact]
    public async Task BindingWithoutApps_StaysAccountWide_ButMcManageDeletesOnlyAppPullZones()
    {
        var binding = new BindingBody("codebahn", Repo, null, "", Workflow, Capabilities: ["mc.read", "mc.manage"]);
        var (key, principal) = await KeyAsync(binding, TagToken());
        // No app scope: the API leaves the field out.
        Assert.False(principal.TryGetProperty("apps", out var apps) && apps.ValueKind != JsonValueKind.Null);

        var list = await (await SendAsync(HttpMethod.Get, Mc, key)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("other-app", list);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Mc}/app-other", key)).StatusCode);
        // mc.manage stands in for pullzone.write only on a pull zone Bunny linked to an existing app.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/20", key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, "/proxy/api.bunny.net/pullzone/22", key)).StatusCode);
    }

    [Fact]
    public async Task OnePullRequestBinding_CanHoldBothASiteAndAnApp()
    {
        var binding = PullRequestBinding("mc.read", "sites.read") with { SitePrefix = "example-site-pr-" };
        var (_, principal) = await KeyAsync(binding, PullRequestToken());
        Assert.Equal("example-app-pr-7", principal.GetProperty("apps")[0].GetString());
        Assert.Equal("example-site-pr-7", principal.GetProperty("sites")[0].GetString());
    }

    [Theory]
    [InlineData("mc_without_app_prefix")]
    [InlineData("sites_without_site_prefix")]
    [InlineData("account_capability")]
    [InlineData("apps_on_pull_request_binding")]
    [InlineData("app_without_mc")]
    [InlineData("bad_app_name")]
    public async Task Admin_AppBindingsStayNarrow(string variant)
    {
        var human = await HumanSessionAsync();
        var binding = variant switch
        {
            "mc_without_app_prefix" => PullRequestBinding("sites.read", "mc.manage") with { AppPrefix = null, SitePrefix = "example-site-pr-" },
            "sites_without_site_prefix" => PullRequestBinding("mc.read", "sites.read"),
            "account_capability" => PullRequestBinding("mc.read", "account.read"),
            "apps_on_pull_request_binding" => PullRequestBinding() with { Apps = ["example-app-master"] },
            "app_without_mc" => new BindingBody("codebahn", Repo, null, "", Workflow, Capabilities: ["statistics.read"], Apps: ["example-app-master"]),
            "bad_app_name" => new BindingBody("codebahn", Repo, null, "", Workflow, Capabilities: ["mc.read"], Apps: ["Example_App"]),
            _ => throw new ArgumentException(variant),
        };
        var (status, body) = await CreateBindingAsync(human, binding);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid_binding", body.GetProperty("error").GetString());
    }
}
