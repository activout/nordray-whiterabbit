using System.Text.RegularExpressions;

namespace Nordray.WhiteRabbit.Core.Sites;

/// <summary>
/// Which Bunny Sites a caller may touch. BunnyCLI names a site's storage zone and pull zone
/// <c>sites-&lt;site&gt;-&lt;6 lowercase letters or digits&gt;</c> (see <c>siteResourcePattern</c> in BunnyCLI), so a zone
/// belongs to a site exactly when its name has that form. Sites imported with <c>bunny sites create --from-zone</c>
/// keep their old zone names and are therefore never in a scope.
/// </summary>
public sealed partial class SiteScope
{
    private readonly HashSet<string> _sites;

    private SiteScope(IEnumerable<string> sites) => _sites = sites.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every site on the account (a human session, or a binding that also holds pullzone.*).</summary>
    public static readonly SiteScope? Unrestricted = null;

    public static SiteScope Of(IEnumerable<string> sites) => new(sites);

    public IReadOnlyCollection<string> Sites => _sites;

    /// <summary>Whether a storage zone or pull zone name belongs to one of the sites.</summary>
    public bool Contains(string? zoneName) => SiteOf(zoneName) is { } site && _sites.Contains(site);

    /// <summary>The site a zone name belongs to, or null when it is not a site zone.</summary>
    public static string? SiteOf(string? zoneName)
    {
        if (zoneName is null) return null;
        var match = ZoneName().Match(zoneName);
        return match.Success ? match.Groups["site"].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Whether a zone is reachable for the scope: any site zone when unrestricted, else one of the scope's sites.
    /// A zone that is not a site zone is never reachable through the sites.* capabilities.
    /// </summary>
    public static bool Allows(SiteScope? scope, string? zoneName) =>
        scope is null ? SiteOf(zoneName) is not null : scope.Contains(zoneName);

    /// <summary>BunnyCLI's rule for site names: 3 to 47 lowercase letters, digits and dashes.</summary>
    public static bool IsSiteName(string? name) => name is not null && SiteName().IsMatch(name);

    /// <summary>A preview prefix such as <c>example-site-pr-</c>; the PR number is appended to it.</summary>
    public static bool IsSitePrefix(string? prefix) =>
        prefix is not null && prefix.Length is >= 2 and <= 40 && SitePrefix().IsMatch(prefix);

    /// <summary>The PR number of a pull request ref (<c>refs/pull/N/head</c> on Forgejo, <c>refs/pull/N/merge</c> on GitHub).</summary>
    public static string? PullRequestNumber(string? reference)
    {
        if (reference is null) return null;
        var match = PullRef().Match(reference);
        return match.Success ? match.Groups["number"].Value : null;
    }

    // The site name is greedy but must leave "-" + exactly six [a-z0-9] at the end, so "sites-example-site-pr-5-abc123"
    // belongs to "example-site-pr-5", never to "example-site".
    [GeneratedRegex("^sites-(?<site>[a-z0-9-]{3,47})-[a-z0-9]{6}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZoneName();

    [GeneratedRegex("^[a-z0-9-]{3,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteName();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SitePrefix();

    [GeneratedRegex("^refs/pull/(?<number>[1-9][0-9]{0,9})/(head|merge)$", RegexOptions.CultureInvariant)]
    private static partial Regex PullRef();
}
