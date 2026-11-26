using System.Text.RegularExpressions;

namespace Nordray.WhiteRabbit.Core.Sites;

/// <summary>
/// Which Magic Containers apps a caller may touch, by exact app name. A binding without apps keeps the account-wide
/// mc.* capabilities (null scope); a binding with <c>--app</c>, and every pull request binding with
/// <c>--app-prefix</c>, reaches only its apps.
/// </summary>
public sealed partial class AppScope
{
    private readonly HashSet<string> _apps;

    private AppScope(IEnumerable<string> apps) => _apps = apps.ToHashSet(StringComparer.Ordinal);

    public static AppScope Of(IEnumerable<string> apps) => new(apps);

    public IReadOnlyCollection<string> Apps => _apps;

    /// <summary>Whether the app may be touched: any app when the scope is null, else only the scope's (exact name).</summary>
    public static bool Allows(AppScope? scope, string? appName) =>
        appName is not null && (scope is null || scope._apps.Contains(appName));

    /// <summary>Lowercase letters, digits and dashes, as Magic Containers app names are used here (e.g. example-app-pr-12).</summary>
    public static bool IsAppName(string? name) => name is not null && AppName().IsMatch(name);

    /// <summary>Longest app name accepted here.</summary>
    public const int MaxAppNameLength = 63;

    /// <summary>
    /// A pull request prefix such as <c>example-app-pr-</c>; the PR number is appended to it. Short enough that every
    /// accepted PR number still gives a valid app name.
    /// </summary>
    public static bool IsAppPrefix(string? prefix) =>
        prefix is not null && prefix.Length is >= 2 and <= MaxAppNameLength - SiteScope.MaxPullRequestDigits && AppPrefix().IsMatch(prefix);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$", RegexOptions.CultureInvariant)] // at most MaxAppNameLength
    private static partial Regex AppName();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex AppPrefix();
}
