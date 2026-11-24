using Nordray.WhiteRabbit.Core;

namespace Nordray.WhiteRabbit.Bunny;

/// <summary>
/// Hard-coded registry of every supported proxied bunny.net operation.
/// This is the sole runtime authority for which routes are proxied and what permissions they require.
/// Never add an operation here without a code review. Never auto-generate entries from OpenAPI alone.
/// Incoming paths mirror the bunny.net API path under /proxy/api.bunny.net/.
/// </summary>
public sealed class BunnyOperationRegistry
{
    private const string BunnyApiBase = "https://api.bunny.net";

    /// <summary>The main storage region's host; White Rabbit picks the zone's own region host per request.</summary>
    private const string StorageBase = "https://storage.bunnycdn.com";

    /// <summary>rabbit sends a storage request for <c>https://&lt;host&gt;/&lt;zone&gt;/&lt;path&gt;</c> here.</summary>
    public const string StorageFileTemplate = "/proxy/storage/{storageHost}/{zoneName}/{**path}";

    private static readonly string[] PullZoneSecrets =
        ["ZoneSecurityKey", "AWSSigningKey", "AWSSigningSecret", "LogForwardingToken", "Hostnames[].CertificateKey"];

    private static readonly string[] StorageZoneSecrets =
        ["Password", "ReadOnlyPassword", .. PullZoneSecrets.Select(f => "PullZones[]." + f)];


    private static readonly BunnyOperation[] All =
    [
        // --- Core Platform: Regions (auth-only, no capability required) ---
        new BunnyOperation(
            OperationId: "core.region.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/region",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/region",
            CredentialKind: BunnyCredentialKind.None,
            RequiredCapability: null,
            RequiresAuthenticationOnly: true,
            ConsentTitle: "List regions",
            ConsentDescription: "Read the list of available bunny.net regions."),

        // --- Core Platform: User ---
        // BunnyCLI reads it for `bunny whoami` and for the account ID (`apps deploy` uses it as the registry
        // namespace).
        new BunnyOperation(
            OperationId: "core.user.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/user",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/user",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "account.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read account details",
            ConsentDescription: "Read the account's ID, owner, contact, billing and address details."),

        // --- Core Platform: Statistics ---
        new BunnyOperation(
            OperationId: "core.statistics.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/statistics",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/statistics",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "statistics.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read statistics",
            ConsentDescription: "Read account-level traffic and billing statistics."),

        // --- Core Platform: Pull Zones ---
        // The sites.* capabilities grant these too, but only on the caller's own sites (zones named
        // sites-<site>-<suffix>): lists are filtered, and a zone outside the scope is not found. Secrets in a pull zone
        // (token authentication key, origin signing keys, log forwarding token, custom certificates' private keys)
        // are covered by no capability and always redacted.
        new BunnyOperation(
            OperationId: "core.pullzone.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List pull zones",
            ConsentDescription: "Read the list of pull zones on the account.",
            // The API answers with a plain array or, for some queries, an { Items, HasMoreItems } envelope.
            RedactJsonFields: [.. PullZoneSecrets, .. PullZoneSecrets.Select(f => "Items[]." + f)],
            SiteCapability: "sites.read",
            SiteResource: SiteResource.PullZoneList),

        new BunnyOperation(
            OperationId: "core.pullzone.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read pull zone",
            ConsentDescription: "Read configuration details for a specific pull zone.",
            RedactJsonFields: PullZoneSecrets,
            SiteCapability: "sites.read",
            SiteResource: SiteResource.PullZone),

        new BunnyOperation(
            OperationId: "core.pullzone.add",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Create pull zone",
            ConsentDescription: "Create a new pull zone on the account.",
            RedactJsonFields: PullZoneSecrets,
            SiteCapability: "sites.manage",
            SiteResource: SiteResource.PullZoneCreate,
            // What `bunny sites create` sends: a storage-origin pull zone for the site's own storage zone.
            SiteAllowedBodyFields: ["Name", "StorageZoneId", "OriginType", "EnableGeoZoneUS", "EnableGeoZoneEU",
                "EnableGeoZoneASIA", "EnableGeoZoneSA", "EnableGeoZoneAF"]),

        new BunnyOperation(
            OperationId: "core.pullzone.update",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Update pull zone",
            ConsentDescription: "Update configuration for an existing pull zone.",
            RedactJsonFields: PullZoneSecrets,
            SiteCapability: "sites.deploy",
            SiteResource: SiteResource.PullZone,
            // The site's cache settings, and detaching an edge script: MiddlewareScriptId only as 0, the value BunnyCLI
            // sends, so a site deploy can never attach a script (which could rewrite or redirect the site's traffic).
            SiteAllowedBodyFields: ["CacheControlMaxAgeOverride", "CacheControlPublicMaxAgeOverride", "MiddlewareScriptId=0"]),

        new BunnyOperation(
            OperationId: "core.pullzone.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete pull zone",
            ConsentDescription: "Delete a pull zone from the account.",
            SiteCapability: "sites.manage",
            SiteResource: SiteResource.PullZone,
            AppResource: AppResource.AppPullZone,
            // An app's own CDN pull zone outlives the app; mc.manage may delete it while the app is still there.
            AppCapability: "mc.manage"),

        new BunnyOperation(
            OperationId: "core.pullzone.purge",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{id}/purgeCache",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{id}/purgeCache",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Purge pull zone cache",
            ConsentDescription: "Purge the whole cache of a pull zone.",
            SiteCapability: "sites.deploy",
            SiteResource: SiteResource.PullZone),

        new BunnyOperation(
            OperationId: "core.pullzone.edgerule.upsert",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{pullZoneId}/edgerules/addOrUpdate",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{pullZoneId}/edgerules/addOrUpdate",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Add or update edge rule",
            ConsentDescription: "Add or change an edge rule of a pull zone. A site publishes a deploy by retargeting one.",
            SiteCapability: "sites.deploy",
            SiteResource: SiteResource.PullZone),

        new BunnyOperation(
            OperationId: "core.pullzone.edgerule.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{pullZoneId}/edgerules/{edgeRuleId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{pullZoneId}/edgerules/{edgeRuleId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete edge rule",
            ConsentDescription: "Delete an edge rule of a pull zone.",
            SiteCapability: "sites.deploy",
            SiteResource: SiteResource.PullZone),

        new BunnyOperation(
            OperationId: "core.pullzone.forcessl",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/pullzone/{id}/setForceSSL",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/pullzone/{id}/setForceSSL",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "pullzone.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Force HTTPS on a hostname",
            ConsentDescription: "Turn HTTP-to-HTTPS redirection on or off for a hostname of a pull zone.",
            SiteCapability: "sites.manage",
            SiteResource: SiteResource.PullZone,
            SiteAllowedBodyFields: ["Hostname", "ForceSSL"]),

        // --- Bunny Sites: storage zones ---
        // Only zones that belong to a site (sites-<site>-<suffix>) are reachable, for every caller: White Rabbit does
        // not open up Edge Storage in general. The zone passwords are always redacted; White Rabbit injects them into
        // the storage requests below itself.
        new BunnyOperation(
            OperationId: "sites.storagezone.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/storagezone",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/storagezone",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "sites.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List site storage zones",
            ConsentDescription: "Read the storage zones of your Bunny Sites. Zone passwords are redacted.",
            RedactJsonFields: StorageZoneSecrets,
            SiteResource: SiteResource.StorageZoneList),

        new BunnyOperation(
            OperationId: "sites.storagezone.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/storagezone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/storagezone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "sites.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read site storage zone",
            ConsentDescription: "Read the storage zone of a Bunny Site. Zone passwords are redacted.",
            RedactJsonFields: StorageZoneSecrets,
            SiteResource: SiteResource.StorageZone),

        new BunnyOperation(
            OperationId: "sites.storagezone.add",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/storagezone",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/storagezone",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "sites.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Create site storage zone",
            ConsentDescription: "Create the storage zone of a new Bunny Site.",
            RedactJsonFields: StorageZoneSecrets,
            AllowedBodyFields: ["Name", "Region", "ReplicationRegions", "ZoneTier"],
            SiteResource: SiteResource.StorageZoneCreate),

        new BunnyOperation(
            OperationId: "sites.storagezone.update",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/storagezone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/storagezone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "sites.deploy",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Update site not-found page",
            ConsentDescription: "Set a site storage zone's not-found page, which a publish points at the live deploy.",
            AllowedBodyFields: ["Custom404FilePath", "Rewrite404To200"],
            RedactJsonFields: StorageZoneSecrets,
            SiteResource: SiteResource.StorageZone),

        new BunnyOperation(
            OperationId: "sites.storagezone.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: "/proxy/api.bunny.net/storagezone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/storagezone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "sites.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete site storage zone",
            ConsentDescription: "Delete the storage zone of a Bunny Site with all its files.",
            SiteResource: SiteResource.StorageZone),

        // --- Bunny Sites: files in a site's storage zone ---
        // rabbit's forwarder intercepts BunnyCLI's connections to <region>.storage.bunnycdn.com and sends them here.
        // White Rabbit looks the zone up with the owner's key and injects the zone's password; the caller only ever
        // holds the redaction placeholder.
        new BunnyOperation(
            OperationId: "sites.file.read",
            IncomingMethod: "GET",
            IncomingPathTemplate: StorageFileTemplate,
            DestinationBaseUrl: StorageBase,
            DestinationPathTemplate: "/{zoneName}/{**path}",
            CredentialKind: BunnyCredentialKind.StorageZonePassword,
            RequiredCapability: "sites.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read site files",
            ConsentDescription: "List and download the files of a Bunny Site.",
            SiteResource: SiteResource.StorageFile),

        new BunnyOperation(
            OperationId: "sites.file.upload",
            IncomingMethod: "PUT",
            IncomingPathTemplate: StorageFileTemplate,
            DestinationBaseUrl: StorageBase,
            DestinationPathTemplate: "/{zoneName}/{**path}",
            CredentialKind: BunnyCredentialKind.StorageZonePassword,
            RequiredCapability: "sites.deploy",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Upload site files",
            ConsentDescription: "Upload files of a Bunny Site deploy and its state file.",
            SiteResource: SiteResource.StorageFile),

        new BunnyOperation(
            OperationId: "sites.file.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: StorageFileTemplate,
            DestinationBaseUrl: StorageBase,
            DestinationPathTemplate: "/{zoneName}/{**path}",
            CredentialKind: BunnyCredentialKind.StorageZonePassword,
            RequiredCapability: "sites.deploy",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete site files",
            ConsentDescription: "Delete files of a Bunny Site, e.g. old deploys.",
            SiteResource: SiteResource.StorageFile),

        // --- Core Platform: DNS ---
        new BunnyOperation(
            OperationId: "core.dns.zone.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/dnszone",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/dnszone",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "dns.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List DNS zones",
            ConsentDescription: "Read the list of DNS zones on the account."),

        new BunnyOperation(
            OperationId: "core.dns.zone.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/dnszone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/dnszone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "dns.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read DNS zone",
            ConsentDescription: "Read configuration for a specific DNS zone."),

        new BunnyOperation(
            OperationId: "core.dns.zone.add",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/dnszone",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/dnszone",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "dns.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Create DNS zone",
            ConsentDescription: "Create a new DNS zone on the account."),

        new BunnyOperation(
            OperationId: "core.dns.zone.update",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/dnszone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/dnszone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "dns.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Update DNS zone",
            ConsentDescription: "Update configuration for an existing DNS zone."),

        new BunnyOperation(
            OperationId: "core.dns.zone.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: "/proxy/api.bunny.net/dnszone/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/dnszone/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "dns.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete DNS zone",
            ConsentDescription: "Delete a DNS zone from the account."),

        // --- Shield ---
        new BunnyOperation(
            OperationId: "shield.zone.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/shield/zones",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/shield/zones",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "shield.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List Shield zones",
            ConsentDescription: "Read the list of Shield zones on the account."),

        new BunnyOperation(
            OperationId: "shield.zone.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/shield/zones/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/shield/zones/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "shield.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read Shield zone",
            ConsentDescription: "Read configuration for a specific Shield zone."),

        new BunnyOperation(
            OperationId: "shield.zone.update",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/shield/zones/{id}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/shield/zones/{id}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "shield.write",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Update Shield zone",
            ConsentDescription: "Update configuration for an existing Shield zone."),

        // --- Magic Containers and Bunny Database (Phase 6) ---
        // Reviewed against BunnyCLI 0.22.0's OpenAPI specs. Left out on purpose: log forwarding (its responses carry
        // tokens), registry changes and single-registry reads, separate volume and endpoint changes, and database groups.

        new BunnyOperation(
            OperationId: "mc.app.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List container apps",
            ConsentDescription: "Read the list of Magic Containers apps on the account.",
            AppResource: AppResource.AppList),

        new BunnyOperation(
            OperationId: "mc.app.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read container app",
            ConsentDescription: "Read a Magic Containers app's configuration. Environment variable values are redacted.",
            RedactJsonFields: ["containerTemplates[].environmentVariables[].value"],
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.app.overview",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}/overview",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}/overview",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read container app overview",
            ConsentDescription: "Read a Magic Containers app's status overview.",
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.container.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}/containers/{containerId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}/containers/{containerId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read container",
            ConsentDescription: "Read one container of a Magic Containers app. Environment variable values are redacted.",
            RedactJsonFields: ["environmentVariables[].value"],
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.region.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/regions",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/regions",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List container regions",
            ConsentDescription: "Read the regions Magic Containers apps can run in."),

        new BunnyOperation(
            OperationId: "mc.registry.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/registries",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/registries",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List container registries",
            ConsentDescription: "Read the container registries configured on the account. Password fragments are redacted.",
            RedactJsonFields: ["items[].firstPasswordSymbols", "items[].lastPasswordSymbols"]),

        new BunnyOperation(
            OperationId: "mc.container.update",
            IncomingMethod: "PATCH",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}/containers/{containerId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}/containers/{containerId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.deploy",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Update container",
            ConsentDescription: "Change a container of a Magic Containers app, e.g. its image. This deploys new code.",
            RedactJsonFields: ["environmentVariables[].value"],
            // Only the image: environment variables need mc.env, and endpoints, volumes, probes and the entry point
            // are not covered by any capability.
            AllowedBodyFields: ["image", "imageName", "imageNamespace", "imageTag", "imageDigest", "imageRegistryId", "imagePullPolicy"],
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.app.deploy",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}/deploy",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}/deploy",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.deploy",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Deploy container app",
            ConsentDescription: "Deploy a Magic Containers app.",
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.app.restart",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}/restart",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}/restart",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.deploy",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Restart container app",
            ConsentDescription: "Restart a Magic Containers app.",
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.container.env.set",
            IncomingMethod: "PUT",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}/containers/{containerId}/env",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}/containers/{containerId}/env",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.env",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Set container environment variables",
            ConsentDescription: "Replace a container's environment variables. Values sent back redacted keep their current value; new values are never shown again.",
            RedactJsonFields: ["environmentVariables[].value"],
            RestoreRedactedEnvironmentFrom: "/mc/apps/{appId}/containers/{containerId}",
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.app.create",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Create container app",
            ConsentDescription: "Create a Magic Containers app, e.g. a preview deployment.",
            RedactJsonFields: ["containerTemplates[].environmentVariables[].value"],
            AppResource: AppResource.AppCreate),

        new BunnyOperation(
            OperationId: "mc.app.update",
            IncomingMethod: "PUT",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Replace container app",
            ConsentDescription: "Replace a Magic Containers app's whole configuration: containers, images, environment variables, endpoints, scaling and regions. Values sent back redacted keep their current value.",
            RedactJsonFields: ["containerTemplates[].environmentVariables[].value"],
            RestoreRedactedAppEnvironmentFrom: "/mc/apps/{appId}",
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "mc.app.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: "/proxy/api.bunny.net/mc/apps/{appId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/mc/apps/{appId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "mc.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete container app",
            ConsentDescription: "Delete a Magic Containers app and its containers, e.g. to clean up a preview deployment.",
            AppResource: AppResource.App),

        new BunnyOperation(
            OperationId: "db.config.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v1/config",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v1/config",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read database configuration",
            ConsentDescription: "Read Bunny Database regions and configuration."),

        new BunnyOperation(
            OperationId: "db.config.limits",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v1/config/limits",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v1/config/limits",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read database limits",
            ConsentDescription: "Read Bunny Database account limits."),

        new BunnyOperation(
            OperationId: "db.config.optimal",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v1/config/optimal",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v1/config/optimal",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read optimal database regions",
            ConsentDescription: "Read the recommended Bunny Database regions."),

        new BunnyOperation(
            OperationId: "db.database.list",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v2/databases",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v2/databases",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "List databases",
            ConsentDescription: "Read the list of Bunny Database databases."),

        new BunnyOperation(
            OperationId: "db.live.metrics",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v1/live/live_db",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v1/live/live_db",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read database status",
            ConsentDescription: "Read whether Bunny Database databases are active, as `bunny db list` shows it.",
            // A read despite POST: the body only names the databases ({"db_ids": [...]}).
            AllowedBodyFields: ["db_ids"]),

        new BunnyOperation(
            OperationId: "db.database.get",
            IncomingMethod: "GET",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v2/databases/{dbId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v2/databases/{dbId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.read",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Read database",
            ConsentDescription: "Read a Bunny Database database's details."),

        new BunnyOperation(
            OperationId: "db.database.create",
            IncomingMethod: "POST",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v2/databases",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v2/databases",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Create database",
            ConsentDescription: "Create a Bunny Database database."),

        new BunnyOperation(
            OperationId: "db.database.delete",
            IncomingMethod: "DELETE",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v2/databases/{dbId}",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v2/databases/{dbId}",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.manage",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Delete database",
            ConsentDescription: "Delete a Bunny Database and its data, e.g. to clean up a preview deployment."),

        new BunnyOperation(
            OperationId: "db.token.generate",
            IncomingMethod: "PUT",
            IncomingPathTemplate: "/proxy/api.bunny.net/database/v2/databases/{dbId}/auth/generate",
            DestinationBaseUrl: BunnyApiBase,
            DestinationPathTemplate: "/database/v2/databases/{dbId}/auth/generate",
            CredentialKind: BunnyCredentialKind.AccountApiKey,
            RequiredCapability: "db.token",
            RequiresAuthenticationOnly: false,
            ConsentTitle: "Create database token",
            ConsentDescription: "Create an access token for a Bunny Database database. The token grants direct access to that database."),
    ];

    private static readonly CapabilityDescription[] Capabilities =
    [
        new("account.read", "Read account details",
            "Read the account's ID, the owner's name and email, roles, balance, billing details and address. " +
            "`bunny whoami` and `bunny apps deploy` need it."),
        new("statistics.read", "Read statistics", "Read account-level traffic and billing statistics."),
        new("pullzone.read", "Read pull zones", "List the pull zones on the account and read their configuration."),
        new("pullzone.write", "Manage pull zones", "Create, change and delete any pull zone on the account."),
        new("sites.read", "Read your Bunny Sites",
            "Read the binding's Bunny Sites: their storage zones, pull zones, deploys and files. A human session sees " +
            "every site. Zone passwords and pull zone secrets are never shown."),
        new("sites.deploy", "Deploy your Bunny Sites",
            "Upload and delete files of the binding's Bunny Sites, publish a deploy (edge rules, cache settings, not-found " +
            "page) and purge the cache. This changes what the site serves."),
        new("sites.manage", "Create and delete your Bunny Sites",
            "Create and delete the binding's Bunny Sites, with their storage zone, pull zone and files, e.g. per-PR previews."),
        new("dns.read", "Read DNS zones", "List the DNS zones on the account and read their configuration and records."),
        new("dns.write", "Manage DNS zones", "Create, change and delete any DNS zone on the account."),
        new("shield.read", "Read Shield zones", "List the Shield zones on the account and read their configuration."),
        new("shield.write", "Change Shield zones", "Change the configuration of any Shield zone on the account."),
        new("mc.read", "Read container apps",
            "List every Magic Containers app on the account and read its configuration, containers, status, regions " +
            "and the account's container registries. Environment variable values and registry password fragments are redacted."),
        new("mc.deploy", "Deploy container apps",
            "Change the image of any container of any Magic Containers app on the account, deploy and restart apps. " +
            "This runs new code with the app's existing secrets."),
        new("mc.env", "Set container environment variables",
            "Replace the environment variables of any container on the account. Current values are never shown, but " +
            "they can be overwritten or removed, and the new values reach the running code."),
        new("mc.manage", "Create, replace and delete container apps",
            "Create Magic Containers apps, replace the whole configuration of any app on the account (containers, " +
            "images, environment variables, endpoints, scaling, regions), and delete any app with its containers, " +
            "e.g. preview deployments. Current environment values are never shown."),
        new("db.read", "Read databases",
            "List every Bunny Database database on the account and read its details, the account's limits and the available regions."),
        new("db.manage", "Create and delete databases",
            "Create Bunny Database databases, and delete any database on the account with all its data."),
        new("db.token", "Create database tokens",
            "Create full-access tokens for any Bunny Database database on the account. A token gives direct access to that database's data."),
    ];

    public IReadOnlyList<BunnyOperation> GetAll() => All;

    /// <summary>Every capability an operation requires, with the description shown to the person approving it.</summary>
    public IReadOnlyList<CapabilityDescription> GetCapabilities() => Capabilities;

    /// <summary>
    /// Finds a registered operation by matching the HTTP method and full incoming path
    /// (e.g. GET /proxy/api.bunny.net/pullzone/123). Path template parameters like {id}
    /// match any single non-empty path segment.
    /// </summary>
    public BunnyOperation? FindByRequest(string method, string path)
    {
        var upperMethod = method.ToUpperInvariant();
        foreach (var op in All)
        {
            if (!string.Equals(op.IncomingMethod, upperMethod, StringComparison.Ordinal)) continue;
            if (MatchesTemplate(op.IncomingPathTemplate, path)) return op;
        }
        return null;
    }

    private static bool MatchesTemplate(string template, string path)
    {
        var templateSegments = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // A trailing {**name} matches the rest of the path, including nothing (a storage zone's root listing).
        var catchAll = templateSegments.Length > 0 && templateSegments[^1].StartsWith("{**", StringComparison.Ordinal);
        var fixedCount = catchAll ? templateSegments.Length - 1 : templateSegments.Length;
        if (catchAll ? pathSegments.Length < fixedCount : pathSegments.Length != fixedCount) return false;

        for (var i = 0; i < fixedCount; i++)
        {
            var t = templateSegments[i];
            if (t.StartsWith('{') && t.EndsWith('}')) continue; // route parameter — matches any segment
            if (!string.Equals(t, pathSegments[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
