using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Nordray.WhiteRabbit.Core;
using Nordray.WhiteRabbit.Core.Federation;
using Nordray.WhiteRabbit.Core.Models;
using Nordray.WhiteRabbit.Core.Sites;
using Nordray.WhiteRabbit.Infrastructure.Database;
using Nordray.WhiteRabbit.Web.Cli;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Nordray.WhiteRabbit.Web.Proxy;

/// <summary>
/// Decides every Bunny proxy request before YARP forwards it. A request authenticates with a virtual credential in
/// <c>AccessKey</c> (from <c>/api/cli/credentials</c>), which resolves to an owner and a set of effective
/// capabilities; then the registry's capability is required and
/// the owner's own Bunny key is injected server side. The inbound <c>AccessKey</c> and <c>Authorization</c> headers
/// are always removed by the route transforms, so neither is forwarded upstream.
/// </summary>
public sealed class BunnyProxyGate(IDataProtectionProvider dataProtection)
{
    public const string AccessKeyItem = "bunny.access-key";
    /// <summary>The owner's own bunny.net key, for White Rabbit's own lookups (e.g. a storage zone's password).</summary>
    public const string OwnerKeyItem = "bunny.owner-key";
    /// <summary>Set when the request must stay within the caller's sites: a <see cref="SiteAccess"/>.</summary>
    public const string SiteAccessItem = "bunny.site-access";
    /// <summary>Set when the request must stay within the caller's Magic Containers apps: an <see cref="AppAccess"/>.</summary>
    public const string AppAccessItem = "bunny.app-access";
    /// <summary>Data Protection purpose for the stored Bunny API keys. Changing it makes every stored key unreadable.</summary>
    public const string KeyPurpose = "BunnyApiKey.v1";
    private readonly IDataProtector _bunnyKeyProtector = dataProtection.CreateProtector(KeyPurpose);

    private sealed record Caller(User Owner, IReadOnlySet<string> Capabilities, SiteScope? Sites, AppScope? Apps, SecurityEvent Audit);

    public async Task InvokeAsync(HttpContext ctx, Func<Task> next, RouteModel route)
    {
        var metadata = route.Config.Metadata!;
        var operationId = route.Config.RouteId["bunny.".Length..];
        var audit = ctx.RequestServices.GetRequiredService<SecurityAudit>();
        var now = DateTime.UtcNow;

        var (caller, denial) = await FromVirtualKeyAsync(ctx, now);

        if (caller is null)
        {
            var (status, error, describe) = denial!.Value;
            await audit.RecordAsync(describe with { OperationId = operationId, HttpStatus = status });
            await WriteError(ctx, status, error);
            return;
        }

        var allowedEvent = caller.Audit with { EventType = "proxy.request", OperationId = operationId };

        // Capability check: every operation not marked authentication-only needs its capability. A site-aware
        // operation is also granted by its sites.* capability, but then only on the caller's own sites.
        if (metadata["AuthOnly"] != "true")
        {
            var capability = metadata["Capability"];
            var siteCapability = metadata.GetValueOrDefault("SiteCapability") ?? "";
            var appCapability = metadata.GetValueOrDefault("AppCapability") ?? "";
            var direct = caller.Capabilities.Contains(capability);
            var viaSite = !direct && siteCapability.Length > 0 && caller.Capabilities.Contains(siteCapability);
            var viaApp = !direct && !viaSite && appCapability.Length > 0 && caller.Capabilities.Contains(appCapability);
            if (!direct && !viaSite && !viaApp)
            {
                var alternatives = new[] { siteCapability, appCapability }.Where(c => c.Length > 0).ToArray();
                await audit.RecordAsync(allowedEvent with { Outcome = SecurityEvent.Denied, Reason = "capability_not_granted", HttpStatus = 403 });
                await WriteError(ctx, 403, alternatives.Length > 0
                    ? System.Text.Json.JsonSerializer.Serialize(new { error = "capability_not_granted", required = capability, or = string.Join(",", alternatives) })
                    : $$"""{"error":"capability_not_granted","required":"{{capability}}"}""");
                return;
            }
            // mc.* stays within the caller's apps when it has any; mc.manage on an app's CDN zone always checks the app.
            var appResource = metadata.GetValueOrDefault("AppResource") ?? nameof(AppResource.None);
            var appCheck = viaApp
                || (caller.Apps is not null && appResource != nameof(AppResource.None) && appResource != nameof(AppResource.AppPullZone));
            if (appCheck)
                ctx.Items[AppAccessItem] = new AppAccess(caller.Apps, viaApp);
            // The sites.* operations themselves always stay within sites; pull zone operations only when granted that way.
            var siteResource = metadata.GetValueOrDefault("SiteResource") ?? nameof(SiteResource.None);
            if (siteResource != nameof(SiteResource.None) && (viaSite || capability.StartsWith("sites.", StringComparison.Ordinal)))
                ctx.Items[SiteAccessItem] = new SiteAccess(caller.Sites, viaSite);
        }

        // Inject the owner's own bunny.net API key via Items, so the route transform adds it after header copying. A
        // storage request gets the zone's password instead (ScopePolicy looks it up with the owner's key).
        if (metadata["CredentialKind"] is "AccountApiKey" or "StorageZonePassword")
        {
            var reason = TryUnprotectKey(caller.Owner, out var plaintextKey);
            if (reason is not null)
            {
                await audit.RecordAsync(allowedEvent with { Outcome = SecurityEvent.Denied, Reason = reason, HttpStatus = 403 });
                await WriteError(ctx, 403, """{"error":"bunny_api_key_not_configured","detail":"Run rabbit admin key set to add your bunny.net API key."}""");
                return;
            }
            ctx.Items[OwnerKeyItem] = plaintextKey;
            if (metadata["CredentialKind"] == "AccountApiKey") ctx.Items[AccessKeyItem] = plaintextKey;
        }

        await next();

        // A forwarding failure (unreachable upstream, timeout) has no upstream status. Neither has a request White
        // Rabbit refused after authorising it (e.g. a body field that is not allowed). When White Rabbit replaced
        // Bunny's answer (e.g. an unredactable body), Bunny's own status is kept next to the one the caller got.
        var forwardError = ctx.Features.Get<IForwarderErrorFeature>();
        var localReason = ctx.Items[ProxyRequestBodyPolicy.LocalReasonItem] as string;
        var upstreamStatus = ctx.Items[ProxyRequestBodyPolicy.UpstreamStatusItem] as int?;
        if (forwardError is null && localReason is null) upstreamStatus = ctx.Response.StatusCode;
        await audit.RecordAsync(allowedEvent with
        {
            Outcome = SecurityEvent.Allowed,
            HttpStatus = ctx.Response.StatusCode,
            UpstreamStatus = forwardError is null ? upstreamStatus : null,
            Reason = forwardError is not null ? "upstream_" + forwardError.Error.ToString().ToLowerInvariant() : localReason,
        });
    }

    private async Task<(Caller?, (int, string, SecurityEvent)?)> FromVirtualKeyAsync(HttpContext ctx, DateTime now)
    {
        var key = ctx.Request.Headers["AccessKey"].ToString();
        var denied = new SecurityEvent { EventType = "proxy.request", Outcome = SecurityEvent.Denied };

        if (string.IsNullOrEmpty(key))
            return (null, (401, """{"error":"unauthenticated"}""", denied with { Reason = "unauthenticated" }));
        // Anything that is not a White Rabbit virtual key, including a real Bunny key, is refused outright.
        if (!OpaqueToken.LooksLike(key, OpaqueToken.VirtualKeyPrefix))
            return (null, (401, """{"error":"invalid_access_key"}""", denied with { Reason = "invalid_access_key" }));

        var credentials = ctx.RequestServices.GetRequiredService<IVirtualCredentialDao>();
        var credential = await credentials.FindByKeyHashAsync(OpaqueToken.Hash(key));
        if (credential is null)
            return (null, (401, """{"error":"invalid_access_key"}""", denied with { Reason = "unknown_credential" }));
        denied = denied with { CredentialId = credential.Id, SessionId = credential.SessionId };
        if (credential.RevokedUtc is not null)
            return (null, (401, """{"error":"invalid_access_key"}""", denied with { Reason = "credential_revoked" }));
        if (credential.ExpiresUtc <= now)
            return (null, (401, """{"error":"access_key_expired"}""", denied with { Reason = "credential_expired" }));

        var session = await ctx.RequestServices.GetRequiredService<ICliSessionDao>().FindAsync(credential.SessionId);
        if (session is null)
            return (null, (401, """{"error":"invalid_access_key"}""", denied with { Reason = "session_missing" }));

        var resolved = await ctx.RequestServices.GetRequiredService<CliPrincipalResolver>().ResolveAsync(session, now);
        if (resolved.Principal is null)
        {
            var describe = denied with
            {
                Reason = resolved.DenialReason,
                PrincipalType = session.PrincipalType,
                Subject = session.Subject,
                UserId = session.UserId,
                BindingId = session.BindingId,
            };
            return (null, (403, $$"""{"error":"access_denied","reason":"{{resolved.DenialReason}}"}""", describe));
        }

        // Never broader than what the credential was issued for, nor than what the principal may do now.
        var effective = NameList.Parse(credential.Capabilities).Where(resolved.Principal.Capabilities.Contains).ToHashSet(StringComparer.Ordinal);
        var audit = resolved.Principal.Describe("proxy.request", SecurityEvent.Allowed) with { CredentialId = credential.Id };
        return (new Caller(resolved.Principal.Owner, effective, resolved.Principal.Sites, resolved.Principal.Apps, audit), null);
    }

    /// <summary>Returns a denial reason, or null with the decrypted key.</summary>
    private string? TryUnprotectKey(User owner, out string plaintextKey)
    {
        plaintextKey = "";
        if (string.IsNullOrEmpty(owner.BunnyApiKey))
            return "no_api_key";
        try
        {
            plaintextKey = _bunnyKeyProtector.Unprotect(owner.BunnyApiKey);
            return null;
        }
        catch (CryptographicException)
        {
            return "key_decryption_failed";
        }
    }

    private static Task WriteError(HttpContext ctx, int status, string json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(json);
    }
}

/// <summary>
/// The request must stay within <paramref name="Scope"/> (null: any site). <paramref name="ViaSiteCapability"/>: granted by
/// a sites.* capability standing in for pullzone.*, so the operation's site body rules apply too.
/// </summary>
public sealed record SiteAccess(SiteScope? Scope, bool ViaSiteCapability);

/// <summary>
/// The request must stay within <paramref name="Scope"/> (null: any app). <paramref name="ViaAppCapability"/>: a pull zone
/// operation granted by mc.manage, allowed only on the CDN zone of an app within the scope.
/// </summary>
public sealed record AppAccess(AppScope? Scope, bool ViaAppCapability);
