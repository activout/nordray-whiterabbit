using Nordray.WhiteRabbit.Core.Models;
using Nordray.WhiteRabbit.Core.Sites;

namespace Nordray.WhiteRabbit.Core.Federation;

/// <summary>
/// Which CI identities a workload binding accepts. Every compared value must match exactly; there are no patterns.
/// A binding without a ref accepts any branch or tag. Its workflow is then <c>&lt;repository&gt;/&lt;path&gt;</c>, and the
/// repository's own protections (who may push which refs, GitHub environments) decide which refs are trusted.
/// <para>
/// A pull request binding (<see cref="WorkloadBinding.SitePrefix"/> and/or <see cref="WorkloadBinding.AppPrefix"/>) is
/// the one exception to "no pull requests": it accepts only <c>pull_request</c> jobs, and only the sites.* and mc.*
/// capabilities of its prefixes. The session reaches exactly one site and/or one app, <c>&lt;prefix&gt;&lt;PR number&gt;</c>,
/// with the number taken from the token's ref. Fork pull requests get no OIDC token from GitHub or Forgejo, so such a
/// job comes from a branch in the repository itself; it may have changed its own workflow, which is why it can never
/// reach anything but its own PR's site and app.
/// </para>
/// </summary>
public static class BindingRules
{
    /// <summary>The pull request event, allowed only on pull request bindings.</summary>
    public const string PullRequestEvent = "pull_request";

    /// <summary>Events that run code from someone other than the repository's maintainers. Never allowed.</summary>
    public static readonly IReadOnlySet<string> ForbiddenEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "pull_request_target", "pull_request_review", "pull_request_review_comment",
        "issue_comment", "fork", "workflow_run",
    };

    /// <summary>The capabilities a site prefix scopes.</summary>
    public static readonly IReadOnlySet<string> SiteCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "sites.read", "sites.deploy", "sites.manage",
    };

    /// <summary>The capabilities an app scope (apps, app prefix) limits.</summary>
    public static readonly IReadOnlySet<string> AppCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        "mc.read", "mc.deploy", "mc.env", "mc.manage",
    };

    /// <summary>A binding for pull_request jobs: it has a site prefix, an app prefix, or both.</summary>
    public static bool IsPullRequestBinding(WorkloadBinding b) => b.SitePrefix is not null || b.AppPrefix is not null;

    public static readonly IReadOnlyList<string> DefaultEvents = ["push", "workflow_dispatch"];

    public sealed record MatchResult(WorkloadBinding? Binding, string? DenialReason);

    /// <summary>
    /// The one active binding that accepts the identity, or a denial reason. Several matching bindings are an
    /// ambiguity and are denied rather than merged.
    /// </summary>
    public static MatchResult Match(WorkloadIdentity identity, IEnumerable<WorkloadBinding> bindings)
    {
        if (CheckIdentity(identity) is { } invalid)
            return new MatchResult(null, invalid);

        var matches = bindings.Where(b => b.RevokedUtc is null && Accepts(b, identity)).Take(2).ToList();
        return matches.Count switch
        {
            // Without a pull request binding, a pull request job is refused for what it is.
            0 when identity.EventName == PullRequestEvent => new MatchResult(null, "event_not_allowed"),
            0 => new MatchResult(null, "no_matching_binding"),
            1 => new MatchResult(matches[0], null),
            _ => new MatchResult(null, "ambiguous_binding"),
        };
    }

    private static string? CheckIdentity(WorkloadIdentity id)
    {
        if (string.IsNullOrEmpty(id.Repository) || string.IsNullOrEmpty(id.Ref) || string.IsNullOrEmpty(id.WorkflowRef)
            || string.IsNullOrEmpty(id.EventName))
            return "missing_claims";
        if (id.IssuerKind == IssuerKind.GitHub && string.IsNullOrEmpty(id.RepositoryId))
            return "missing_claims";
        if (ForbiddenEvents.Contains(id.EventName))
            return "event_not_allowed";
        if (id.EventName == PullRequestEvent)
        {
            // A pull request job always builds refs/pull/<N>/head (Forgejo) or /merge (GitHub).
            if (SiteScope.PullRequestNumber(id.Ref) is null) return "event_not_allowed";
        }
        else if (!IsBranchOrTag(id.Ref))
            return "ref_not_allowed";
        // The workflow definition must come from the ref being built, so a branch cannot run another ref's workflow.
        if (!id.WorkflowRef.EndsWith("@" + id.Ref, StringComparison.Ordinal))
            return "workflow_ref_mismatch";
        return null;
    }

    private static bool Accepts(WorkloadBinding b, WorkloadIdentity id)
    {
        if (!string.Equals(b.Issuer, id.IssuerName, StringComparison.Ordinal)) return false;
        if (!string.Equals(b.Repository, id.Repository, StringComparison.Ordinal)) return false;
        // CheckIdentity has made sure the identity's workflow_ref ends with "@" + its ref.
        var workflowRef = b.Ref.Length == 0 ? b.WorkflowRef + "@" + id.Ref : b.WorkflowRef;
        if (b.Ref.Length > 0 && !string.Equals(b.Ref, id.Ref, StringComparison.Ordinal)) return false;
        if (!string.Equals(workflowRef, id.WorkflowRef, StringComparison.Ordinal)) return false;
        if (!NameList.Parse(b.EventNames).Contains(id.EventName!)) return false;
        // Pull requests only for pull request bindings, and pull request bindings only for pull requests.
        if ((id.EventName == PullRequestEvent) != IsPullRequestBinding(b)) return false;

        if (id.IssuerKind == IssuerKind.GitHub)
        {
            // Immutable IDs: a deleted and re-created repository or owner with the same name does not match.
            if (!string.Equals(b.RepositoryId, id.RepositoryId, StringComparison.Ordinal)) return false;
            if (b.RepositoryOwnerId is not null && !string.Equals(b.RepositoryOwnerId, id.RepositoryOwnerId, StringComparison.Ordinal)) return false;
            if (b.JobWorkflowRef is not null && !string.Equals(b.JobWorkflowRef, id.JobWorkflowRef, StringComparison.Ordinal)) return false;
            if (b.Environment is not null && !string.Equals(b.Environment, id.Environment, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// The sites a session from this binding and identity may touch: the PR's preview site for a pull request binding,
    /// else the binding's sites (none when it has no sites.* capability).
    /// </summary>
    public static IReadOnlyList<string> SitesFor(WorkloadBinding b, WorkloadIdentity id)
    {
        if (IsPullRequestBinding(b))
            return b.SitePrefix is not null && SiteScope.PullRequestNumber(id.Ref) is { } number ? [b.SitePrefix + number] : [];
        return NameList.Parse(b.Sites).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The apps a session from this binding and identity may touch: the PR's app for a pull request binding (none
    /// without an app prefix), else the binding's apps. Null: the binding names no apps, so mc.* stays account-wide.
    /// </summary>
    public static IReadOnlyList<string>? AppsFor(WorkloadBinding b, WorkloadIdentity id)
    {
        if (IsPullRequestBinding(b))
            return b.AppPrefix is not null && SiteScope.PullRequestNumber(id.Ref) is { } number ? [b.AppPrefix + number] : [];
        var apps = NameList.Parse(b.Apps);
        return apps.Count == 0 ? null : apps.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Validation of a binding before it is stored. Returns problems; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(WorkloadBinding b, IssuerKind kind, IReadOnlySet<string> knownCapabilities)
    {
        var errors = new List<string>();
        if (!IsRepositoryName(b.Repository)) errors.Add("repository must be owner/name");
        if (b.Ref.Length > 0 && !IsBranchOrTag(b.Ref)) errors.Add("ref must start with refs/heads/ or refs/tags/, or be empty for any ref");
        var workflowPath = b.Ref.Length > 0 && b.WorkflowRef.EndsWith("@" + b.Ref, StringComparison.Ordinal)
            ? b.WorkflowRef[..^(b.Ref.Length + 1)]
            : b.WorkflowRef;
        if (!workflowPath.StartsWith(b.Repository + "/", StringComparison.Ordinal) || workflowPath.Length == b.Repository.Length + 1
            || workflowPath.Contains('@') || (b.Ref.Length > 0 && workflowPath == b.WorkflowRef))
            errors.Add("workflow_ref must be <repository>/<workflow path>@<ref>, or <repository>/<workflow path> without a ref");

        var events = NameList.Parse(b.EventNames);
        if (events.Count == 0) errors.Add("at least one event is required");
        foreach (var e in events.Where(ForbiddenEvents.Contains)) errors.Add($"event '{e}' runs untrusted code and cannot be allowed");

        var capabilities = NameList.Parse(b.Capabilities);
        foreach (var c in capabilities.Where(c => !knownCapabilities.Contains(c))) errors.Add($"unknown capability '{c}'");

        var sites = NameList.Parse(b.Sites);
        foreach (var site in sites.Where(s => !SiteScope.IsSiteName(s)))
            errors.Add($"site '{site}' is not a site name (3 to 47 lowercase letters, digits and dashes, starting and ending with a letter or digit)");
        var apps = NameList.Parse(b.Apps);
        foreach (var app in apps.Where(a => !AppScope.IsAppName(a)))
            errors.Add($"app '{app}' is not an app name (lowercase letters, digits and dashes)");
        if (b.SitePrefix is not null && !SiteScope.IsSitePrefix(b.SitePrefix))
            errors.Add($"site_prefix must be 2 to {47 - SiteScope.MaxPullRequestDigits} lowercase letters, digits and dashes, starting with a letter or digit, e.g. 'example-site-pr-'");
        if (b.AppPrefix is not null && !AppScope.IsAppPrefix(b.AppPrefix))
            errors.Add($"app_prefix must be 2 to {AppScope.MaxAppNameLength - SiteScope.MaxPullRequestDigits} lowercase letters, digits and dashes, starting with a letter or digit, e.g. 'example-app-pr-'");

        if (IsPullRequestBinding(b))
        {
            const string what = "a pull request binding (site_prefix, app_prefix)";
            if (sites.Count > 0 || apps.Count > 0) errors.Add($"{what} cannot also name sites or apps");
            if (b.Ref.Length > 0) errors.Add($"{what} has no ref: pull request refs differ per PR");
            if (events.Count != 1 || !events.Contains(PullRequestEvent))
                errors.Add($"{what} accepts exactly the pull_request event");
            foreach (var c in capabilities)
            {
                if (SiteCapabilities.Contains(c) ? b.SitePrefix is null : !AppCapabilities.Contains(c) || b.AppPrefix is null)
                    errors.Add($"{what} may only hold sites.* with a site_prefix and mc.* with an app_prefix, not '{c}'");
            }
        }
        else
        {
            if (events.Contains(PullRequestEvent))
                errors.Add("event 'pull_request' runs untrusted code: it is allowed only on a pull request binding (site_prefix, app_prefix)");
            if (capabilities.Overlaps(SiteCapabilities) && sites.Count == 0)
                errors.Add("sites.* capabilities need the sites they reach (site), or a site_prefix for pull requests");
            if (sites.Count > 0 && !capabilities.Overlaps(SiteCapabilities))
                errors.Add("site is only used by the sites.* capabilities; add one or leave site out");
            if (apps.Count > 0 && !capabilities.Overlaps(AppCapabilities))
                errors.Add("app is only used by the mc.* capabilities; add one or leave app out");
        }

        if (kind == IssuerKind.GitHub)
        {
            if (string.IsNullOrEmpty(b.RepositoryId) || !b.RepositoryId.All(char.IsAsciiDigit))
                errors.Add("repository_id (numeric) is required for GitHub");
            if (b.RepositoryOwnerId is not null && !b.RepositoryOwnerId.All(char.IsAsciiDigit))
                errors.Add("repository_owner_id must be numeric");
        }
        else
        {
            // Forgejo tokens carry none of these claims; a binding requiring them could never match.
            if (b.RepositoryId is not null || b.RepositoryOwnerId is not null || b.JobWorkflowRef is not null || b.Environment is not null)
                errors.Add("repository_id, repository_owner_id, job_workflow_ref and environment are GitHub-only");
        }
        return errors;
    }

    private static bool IsBranchOrTag(string? reference) =>
        reference is not null
        && ((reference.StartsWith("refs/heads/", StringComparison.Ordinal) && reference.Length > "refs/heads/".Length)
            || (reference.StartsWith("refs/tags/", StringComparison.Ordinal) && reference.Length > "refs/tags/".Length));

    private static bool IsRepositoryName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var parts = name.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }
}
