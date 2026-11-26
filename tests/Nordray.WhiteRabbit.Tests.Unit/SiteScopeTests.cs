using Nordray.WhiteRabbit.Bunny;
using Nordray.WhiteRabbit.Core.Federation;
using Nordray.WhiteRabbit.Core.Models;
using Nordray.WhiteRabbit.Core.Sites;
using Nordray.WhiteRabbit.Proxy;

namespace Nordray.WhiteRabbit.Tests.Unit;

public class SiteScopeTests
{
    [Theory]
    [InlineData("sites-example-site-a1b2c3", "example-site")]
    [InlineData("SITES-Example-Site-A1B2C3", "example-site")]
    [InlineData("sites-example-site-pr-12-a1b2c3", "example-site-pr-12")]
    [InlineData("sites-a-b-c-zzzzzz", "a-b-c")]
    [InlineData("example-site", null)]
    [InlineData("sites-example-site", null)]
    [InlineData("sites-example-site-a1b2c", null)]          // suffix too short
    [InlineData("sites-example-site-a1b2c3d", null)]        // the six-character suffix must follow a dash
    [InlineData("sites-example-site-a1b2c_", null)]
    [InlineData("xsites-example-site-a1b2c3", null)]
    [InlineData("sites--foo-a1b2c3", null)]            // a site name starts and ends with a letter or digit
    [InlineData("sites-foo--a1b2c3", null)]
    [InlineData(null, null)]
    public void SiteOf_ReadsBunnyCliZoneNames(string? zone, string? site) => Assert.Equal(site, SiteScope.SiteOf(zone));

    [Fact]
    public void Scope_ContainsOnlyItsOwnSitesZones()
    {
        var scope = SiteScope.Of(["example-site"]);
        Assert.True(scope.Contains("sites-example-site-a1b2c3"));
        // A preview of the same site is another site: production scope never reaches it, and the other way round.
        Assert.False(scope.Contains("sites-example-site-pr-5-a1b2c3"));
        Assert.False(SiteScope.Of(["example-site-pr-5"]).Contains("sites-example-site-a1b2c3"));
        Assert.False(scope.Contains("sites-other-a1b2c3"));
        Assert.False(scope.Contains("example-site"));
    }

    [Fact]
    public void Unrestricted_ReachesEverySiteZone_ButNoOtherZone()
    {
        Assert.True(SiteScope.Allows(SiteScope.Unrestricted, "sites-any-site-a1b2c3"));
        Assert.False(SiteScope.Allows(SiteScope.Unrestricted, "my-private-storage"));
        Assert.False(SiteScope.Allows(SiteScope.Of([]), "sites-any-site-a1b2c3"));
    }

    [Theory]
    [InlineData("example-site", true)]
    [InlineData("abc", true)]
    [InlineData("-example", false)]
    [InlineData("example-", false)]
    [InlineData("ab", false)]
    public void SiteNames_FollowBunnyCli(string name, bool valid) => Assert.Equal(valid, SiteScope.IsSiteName(name));

    [Fact]
    public void SitePrefix_LeavesRoomForAnyPullRequestNumber()
    {
        var longest = new string('a', 37);
        Assert.True(SiteScope.IsSitePrefix(longest));
        Assert.True(SiteScope.IsSiteName(longest + "1234567890"));
        Assert.False(SiteScope.IsSitePrefix(longest + "a"));
        Assert.False(SiteScope.IsSitePrefix("-pr-"));
    }

    [Theory]
    [InlineData("refs/pull/12/head", "12")]
    [InlineData("refs/pull/12/merge", "12")]
    [InlineData("refs/pull/0/head", null)]
    [InlineData("refs/pull/12", null)]
    [InlineData("refs/heads/pull/12/head", null)]
    [InlineData("refs/pull/12/head/x", null)]
    [InlineData("refs/pull/1a/head", null)]
    public void PullRequestNumber_FromForgejoAndGitHubRefs(string reference, string? number) =>
        Assert.Equal(number, SiteScope.PullRequestNumber(reference));

    [Fact]
    public void Redaction_AppliesToEachElementOfATopLevelArray()
    {
        var json = JsonRedaction.Redact("""[{"Name":"a","ZoneSecurityKey":"s1"},{"Name":"b","ZoneSecurityKey":"s2"}]""", ["ZoneSecurityKey"])!;
        Assert.DoesNotContain("s1", json);
        Assert.DoesNotContain("s2", json);
        Assert.Contains("\"Name\":\"b\"", json);
    }

    [Fact]
    public void Filter_KeepsOnlyAllowedZones_InArraysAndEnvelopes()
    {
        static bool Keep(string? name) => SiteScope.Of(["example-site"]).Contains(name);
        var array = SiteResponses.Filter("""[{"Name":"sites-example-site-a1b2c3"},{"Name":"sites-other-a1b2c3"},{"Id":1}]""", Keep)!;
        Assert.Contains("sites-example-site-a1b2c3", array);
        Assert.DoesNotContain("other", array);
        Assert.DoesNotContain("\"Id\"", array);

        var envelope = SiteResponses.Filter("""{"Items":[{"Name":"sites-other-a1b2c3"}],"HasMoreItems":false}""", Keep)!;
        Assert.DoesNotContain("other", envelope);
        Assert.Contains("HasMoreItems", envelope);

        Assert.Null(SiteResponses.Filter("""{"Name":"sites-example-site-a1b2c3"}""", Keep));
        Assert.Null(SiteResponses.Filter("not json", Keep));
    }

    [Theory]
    [InlineData("/proxy/storage/se.storage.bunnycdn.com/sites-x-a1b2c3/", "sites.file.read")]
    [InlineData("/proxy/storage/se.storage.bunnycdn.com/sites-x-a1b2c3/deploys/abc/assets/app.js", "sites.file.read")]
    [InlineData("/proxy/storage/se.storage.bunnycdn.com", null)]
    public void Registry_CatchAllMatchesAnyDepthBelowTheZone(string path, string? operationId) =>
        Assert.Equal(operationId, new BunnyOperationRegistry().FindByRequest("GET", path)?.OperationId);
}

public class PullRequestBindingTests
{
    private static readonly IReadOnlySet<string> Known =
        new HashSet<string> { "sites.read", "sites.deploy", "sites.manage", "statistics.read" };

    private static WorkloadBinding Preview() => new()
    {
        Id = 9,
        OwnerUserId = 7,
        Issuer = "codebahn",
        Repository = "acme/site",
        Ref = "",
        WorkflowRef = "acme/site/.github/workflows/deploy-preview.yml",
        EventNames = "pull_request",
        Capabilities = "sites.read sites.deploy sites.manage",
        SitePrefix = "site-pr-",
    };

    private static WorkloadIdentity PullRequest(string reference = "refs/pull/12/head", string workflow = "deploy-preview.yml") => new()
    {
        IssuerName = "codebahn",
        IssuerKind = IssuerKind.Forgejo,
        IssuerUrl = "https://codebahn.net/api/actions",
        Subject = "repo:acme/site:pull_request",
        Repository = "acme/site",
        Ref = reference,
        WorkflowRef = $"acme/site/.github/workflows/{workflow}@{reference}",
        EventName = "pull_request",
    };

    [Fact]
    public void PullRequest_MatchesAPullRequestBinding_AndReachesOnlyItsOwnPreviewSite()
    {
        var match = BindingRules.Match(PullRequest(), [Preview()]);
        Assert.Equal(9, match.Binding?.Id);
        Assert.Equal(["site-pr-12"], BindingRules.SitesFor(match.Binding!, PullRequest()));
    }

    [Fact]
    public void PullRequest_WithoutAPullRequestBinding_IsEventNotAllowed()
    {
        var push = Preview() with { SitePrefix = null, EventNames = "pull_request", Sites = "site" };
        Assert.Equal("event_not_allowed", BindingRules.Match(PullRequest(), [push]).DenialReason);
    }

    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("refs/pull/12")]
    public void PullRequest_WithAForgedRef_IsDenied(string reference) =>
        Assert.Null(BindingRules.Match(PullRequest(reference), [Preview()]).Binding);

    [Fact]
    public void PullRequest_FromAnotherWorkflow_DoesNotMatch() =>
        Assert.Equal("event_not_allowed", BindingRules.Match(PullRequest(workflow: "other.yml"), [Preview()]).DenialReason);

    [Fact]
    public void PullRequestBinding_NeverMatchesAPush()
    {
        var push = PullRequest("refs/heads/main") with { EventName = "push" };
        var binding = Preview() with { EventNames = "pull_request push" };
        Assert.Null(BindingRules.Match(push, [binding]).Binding);
    }

    [Fact]
    public void PushBinding_SessionReachesTheBindingsSites() =>
        Assert.Equal(["a-site", "b-site"], BindingRules.SitesFor(Preview() with { SitePrefix = null, Sites = "b-site a-site" }, PullRequest()));

    [Fact]
    public void ValidPullRequestBinding_HasNoProblems() =>
        Assert.Empty(BindingRules.Validate(Preview(), IssuerKind.Forgejo, Known));

    [Theory]
    [InlineData("events")]
    [InlineData("capability")]
    [InlineData("ref")]
    [InlineData("sites")]
    [InlineData("prefix")]
    public void PullRequestBinding_StaysNarrow(string variant)
    {
        var binding = variant switch
        {
            "events" => Preview() with { EventNames = "pull_request push" },
            "capability" => Preview() with { Capabilities = "sites.deploy statistics.read" },
            "ref" => Preview() with { Ref = "refs/heads/main", WorkflowRef = "acme/site/.github/workflows/deploy-preview.yml@refs/heads/main" },
            "sites" => Preview() with { Sites = "site" },
            "prefix" => Preview() with { SitePrefix = "Site_PR" },
            _ => throw new ArgumentException(variant),
        };
        Assert.NotEmpty(BindingRules.Validate(binding, IssuerKind.Forgejo, Known));
    }

    [Fact]
    public void PullRequestEvent_OnAnOrdinaryBinding_IsRejected() =>
        Assert.NotEmpty(BindingRules.Validate(Preview() with { SitePrefix = null, Sites = "site", EventNames = "push pull_request" }, IssuerKind.Forgejo, Known));

    [Fact]
    public void SiteCapabilities_NeedSites()
    {
        var push = Preview() with { SitePrefix = null, EventNames = "push" };
        Assert.NotEmpty(BindingRules.Validate(push, IssuerKind.Forgejo, Known));
        Assert.Empty(BindingRules.Validate(push with { Sites = "site" }, IssuerKind.Forgejo, Known));
        Assert.NotEmpty(BindingRules.Validate(push with { Sites = "Bad_Name" }, IssuerKind.Forgejo, Known));
        Assert.NotEmpty(BindingRules.Validate(push with { Sites = "site", Capabilities = "statistics.read" }, IssuerKind.Forgejo, Known));
    }
}
