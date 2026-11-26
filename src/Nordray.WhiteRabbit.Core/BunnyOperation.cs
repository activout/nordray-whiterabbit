namespace Nordray.WhiteRabbit.Core;

public sealed record BunnyOperation(
    string OperationId,
    string IncomingMethod,
    string IncomingPathTemplate,
    string DestinationBaseUrl,
    string DestinationPathTemplate,
    BunnyCredentialKind CredentialKind,
    string? RequiredCapability,
    bool RequiresAuthenticationOnly,
    string ConsentTitle,
    string ConsentDescription,
    IReadOnlyList<string>? RedactJsonFields = null,
    string? RestoreRedactedEnvironmentFrom = null,
    IReadOnlyList<string>? AllowedBodyFields = null,
    string? RestoreRedactedAppEnvironmentFrom = null,
    string? SiteCapability = null,
    SiteResource SiteResource = SiteResource.None,
    IReadOnlyList<string>? SiteAllowedBodyFields = null,
    AppResource AppResource = AppResource.None,
    string? AppCapability = null)
{
    /// <summary>Which Magic Containers app the request is about, for the app scope check.</summary>
    public AppResource AppResource { get; init; } = AppResource;

    /// <summary>
    /// A capability that also grants the operation, but only for the Magic Containers app it belongs to (within the
    /// caller's app scope). Used for an app's own CDN pull zone, which <c>mc.manage</c> may delete.
    /// </summary>
    public string? AppCapability { get; init; } = AppCapability;

    /// <summary>
    /// A second capability that also grants the operation, but only on the caller's own sites (<see cref="SiteResource"/>
    /// says how the zone is found). A caller with <see cref="RequiredCapability"/> is not limited to sites.
    /// </summary>
    public string? SiteCapability { get; init; } = SiteCapability;

    /// <summary>Which zone the request is about, for the site scope check. None for operations that are not site-aware.</summary>
    public SiteResource SiteResource { get; init; } = SiteResource;

    /// <summary>
    /// Like <see cref="AllowedBodyFields"/>, but only when the operation was granted through <see cref="SiteCapability"/>.
    /// <c>Name=value</c> allows the field only with exactly that value.
    /// </summary>
    public IReadOnlyList<string>? SiteAllowedBodyFields { get; init; } = SiteAllowedBodyFields;

    /// <summary>
    /// JSON fields of a successful response that White Rabbit replaces before the caller sees them, because they hold
    /// secrets the capability does not cover (e.g. container environment variable values). Paths use <c>.</c> between
    /// properties and <c>[]</c> for "every element", e.g. <c>containerTemplates[].environmentVariables[].value</c>.
    /// </summary>
    public IReadOnlyList<string> RedactJsonFields { get; init; } = RedactJsonFields ?? [];

    /// <summary>
    /// For an operation that replaces a container's whole environment (a JSON object of name to value): the upstream
    /// path, with the same route values, of the container whose current <c>environmentVariables</c> fill in values the
    /// caller sends back as the redaction placeholder. Tools such as <c>bunny apps env set</c> read every variable and
    /// write them all back, so without this a redacted read would overwrite every other secret with the placeholder.
    /// </summary>
    public string? RestoreRedactedEnvironmentFrom { get; init; } = RestoreRedactedEnvironmentFrom;

    /// <summary>
    /// When set, the request body must be a JSON object with only these top-level properties; anything else is refused
    /// before forwarding. Used where Bunny's endpoint accepts more than the capability covers, e.g. a container PATCH
    /// that could also replace environment variables, endpoints or volumes.
    /// </summary>
    public IReadOnlyList<string>? AllowedBodyFields { get; init; } = AllowedBodyFields;

    /// <summary>
    /// For an operation that replaces a whole app (<c>containerTemplates[].environmentVariables[]</c>): the upstream
    /// path of the app whose current containers, matched by <c>id</c> or else <c>name</c>, fill in values sent back as
    /// the redaction placeholder. A placeholder anywhere else in the body is refused.
    /// </summary>
    public string? RestoreRedactedAppEnvironmentFrom { get; init; } = RestoreRedactedAppEnvironmentFrom;
}

/// <summary>How a site-aware operation identifies its zone, so the site scope can be checked.</summary>
public enum SiteResource
{
    None,
    /// <summary>GET of a pull zone list: the response is filtered to the scope.</summary>
    PullZoneList,
    /// <summary>A pull zone by its <c>{id}</c> or <c>{pullZoneId}</c> route value.</summary>
    PullZone,
    /// <summary>POST /pullzone: the body's <c>Name</c> and <c>StorageZoneId</c>.</summary>
    PullZoneCreate,
    /// <summary>GET of a storage zone list: filtered to the scope.</summary>
    StorageZoneList,
    /// <summary>A storage zone by its <c>{id}</c> route value.</summary>
    StorageZone,
    /// <summary>POST /storagezone: the body's <c>Name</c>.</summary>
    StorageZoneCreate,
    /// <summary>A file in a storage zone, by the <c>{zoneName}</c> route value. The zone password is injected.</summary>
    StorageFile,
}

/// <summary>How an app-aware operation identifies its Magic Containers app, so the app scope can be checked.</summary>
public enum AppResource
{
    None,
    /// <summary>GET /mc/apps: the response is filtered to the scope.</summary>
    AppList,
    /// <summary>An app by its <c>{appId}</c> route value (and, when the body names the app, that name too).</summary>
    App,
    /// <summary>POST /mc/apps: the body's <c>name</c>.</summary>
    AppCreate,
    /// <summary>A pull zone by <c>{id}</c> that Bunny linked to an app (<c>MagicContainersAppId</c>): its CDN zone.</summary>
    AppPullZone,
}

public enum BunnyCredentialKind
{
    None,
    AccountApiKey,
    StorageZonePassword,
    StreamApiKey
}

/// <summary>
/// What a capability allows, as shown by the CLI and in the capability table. One description covers every operation
/// of the capability, so granting it to a binding never grants more than the text says.
/// </summary>
public sealed record CapabilityDescription(string Name, string Title, string Description);
