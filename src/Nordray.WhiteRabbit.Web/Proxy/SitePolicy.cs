using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Nordray.WhiteRabbit.Core;
using Nordray.WhiteRabbit.Core.Sites;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Nordray.WhiteRabbit.Web.Proxy;

/// <summary>
/// Keeps site-aware requests within the caller's Bunny Sites (<see cref="SiteAccess"/>, set by the gate), before they
/// are forwarded:
/// <list type="bullet">
/// <item>A zone addressed by ID is read upstream with the owner's key; one outside the scope is "not found".</item>
/// <item>A create must name a zone of one of the caller's sites, and a pull zone must use that site's storage zone.</item>
/// <item>Granted through a sites.* capability instead of pullzone.*, only the operation's site body fields are accepted.</item>
/// <item>A storage request gets the zone's own password and region host, looked up by zone name. The caller only ever
/// sees the redaction placeholder in place of the password.</item>
/// </list>
/// Lists are filtered to the scope after the response (<see cref="SiteResponses"/>).
/// </summary>
public sealed class SitePolicy(IForwarderHttpClientFactory clients, IMemoryCache cache)
{
    /// <summary>The checked upstream URI of a storage request, e.g. https://se.storage.bunnycdn.com/zone/path.</summary>
    public const string StorageUriItem = "bunny.storage-uri";

    /// <summary>
    /// The upstream URI for <c>/proxy/storage/&lt;host&gt;/&lt;zone&gt;/&lt;path&gt;</c> (the raw, still encoded path), or null
    /// when the path is not plainly inside the zone: a <c>.</c> or <c>..</c> segment (also percent-encoded), an encoded
    /// slash or backslash, or anything else that leaves the URI outside <c>/&lt;zone&gt;/</c>. Keeps the caller's encoding
    /// and trailing slash (a directory listing).
    /// </summary>
    public static Uri? StorageUri(string rawPath, string query, string host, string zoneName)
    {
        const string prefix = "/proxy/storage/";
        if (!rawPath.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var afterHost = rawPath.IndexOf('/', prefix.Length);
        if (afterHost < 0) return null;
        var zonePath = rawPath[afterHost..];
        var segments = zonePath.Split('/');
        if (segments.Length < 3 || segments[1] != zoneName) return null;
        foreach (var segment in segments.Skip(1))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\') || decoded.Contains('%')) return null;
        }
        if (!Uri.TryCreate($"https://{host}{zonePath}{query}", UriKind.Absolute, out var uri)) return null;
        return uri.Host == host && uri.AbsolutePath.StartsWith("/" + zoneName + "/", StringComparison.Ordinal) ? uri : null;
    }

    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>
    /// A zone password stays in memory this long, so a deploy of many files does not look the zone up for each one.
    /// ponytail: per-process cache; a rotated zone password takes up to a minute to be picked up.
    /// </summary>
    private static readonly TimeSpan ZoneCacheLifetime = TimeSpan.FromMinutes(1);

    /// <summary>Bunny's storage region codes and their HTTP API hosts (the same list as @bunny.net/storage-sdk).</summary>
    public static readonly IReadOnlyDictionary<string, string> StorageHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = "storage.bunnycdn.com",
        ["UK"] = "uk.storage.bunnycdn.com",
        ["NY"] = "ny.storage.bunnycdn.com",
        ["LA"] = "la.storage.bunnycdn.com",
        ["SG"] = "sg.storage.bunnycdn.com",
        ["SE"] = "se.storage.bunnycdn.com",
        ["BR"] = "br.storage.bunnycdn.com",
        ["JH"] = "jh.storage.bunnycdn.com",
        ["SYD"] = "syd.storage.bunnycdn.com",
    };

    private sealed record StorageZoneAccess(string Password, string Host);

    /// <summary>Runs <paramref name="next"/> when the request stays within the scope, or writes an error and stops.</summary>
    public async Task InvokeAsync(HttpContext ctx, RouteModel route, Func<Task> next)
    {
        if (ctx.Items[BunnyProxyGate.SiteAccessItem] is not SiteAccess access)
        {
            await next();
            return;
        }
        var metadata = route.Config.Metadata!;
        var resource = Enum.Parse<SiteResource>(metadata["SiteResource"]);
        var ownerKey = ctx.Items[BunnyProxyGate.OwnerKeyItem] as string;

        if (access.ViaSiteCapability && metadata.GetValueOrDefault("SiteAllowedBodyFields") is { Length: > 0 } fieldList
            && !await CheckBodyFieldsAsync(ctx, fieldList.Split(';', StringSplitOptions.RemoveEmptyEntries)))
            return;

        switch (resource)
        {
            case SiteResource.PullZone:
            {
                var id = RouteValue(ctx, "id") ?? RouteValue(ctx, "pullZoneId");
                if (!SiteScope.Allows(access.Scope, await ZoneNameAsync(ctx, ownerKey, "/pullzone/", id)))
                {
                    await NotInScope(ctx);
                    return;
                }
                break;
            }
            case SiteResource.StorageZone:
            {
                if (!SiteScope.Allows(access.Scope, await ZoneNameAsync(ctx, ownerKey, "/storagezone/", RouteValue(ctx, "id"))))
                {
                    await NotInScope(ctx);
                    return;
                }
                break;
            }
            case SiteResource.StorageZoneCreate:
            case SiteResource.PullZoneCreate:
            {
                var body = await ReadBodyAsync(ctx);
                var name = body is null ? null : Text(body, "Name");
                if (!SiteScope.Allows(access.Scope, name))
                {
                    await Refuse(ctx, 403, "site_not_in_scope",
                        """{"error":"site_not_in_scope","detail":"The new zone's name is not sites-<site>-<suffix> for one of your sites."}""");
                    return;
                }
                if (resource == SiteResource.PullZoneCreate)
                {
                    // The site's pull zone serves the same site's storage zone (origin type 2), nothing else.
                    var storageZoneId = body is null ? null : Property(body, "StorageZoneId")?.ToString();
                    var storageName = await ZoneNameAsync(ctx, ownerKey, "/storagezone/", storageZoneId);
                    var originType = body is null ? null : Property(body, "OriginType")?.ToString();
                    if (SiteScope.SiteOf(storageName) != SiteScope.SiteOf(name) || originType != "2")
                    {
                        await Refuse(ctx, 403, "site_not_in_scope",
                            """{"error":"site_not_in_scope","detail":"A site's pull zone must use that site's own storage zone as its origin."}""");
                        return;
                    }
                }
                break;
            }
            case SiteResource.StorageFile:
            {
                var zoneName = RouteValue(ctx, "zoneName");
                var storageHost = RouteValue(ctx, "storageHost");
                if (storageHost is null || !StorageHosts.Values.Contains(storageHost, StringComparer.OrdinalIgnoreCase)
                    || !SiteScope.Allows(access.Scope, zoneName))
                {
                    await NotInScope(ctx);
                    return;
                }
                var zone = await StorageZoneAsync(ctx, ownerKey, zoneName!);
                if (zone is null)
                {
                    await NotInScope(ctx);
                    return;
                }
                // The upstream URI is built and checked here, after the scope check, so nothing in the path can move
                // the request to another zone than the one whose password it carries.
                var uri = StorageUri(ctx.Request.Path.ToUriComponent(), ctx.Request.QueryString.ToUriComponent(), zone.Host, zoneName!);
                if (uri is null)
                {
                    await Refuse(ctx, 400, "invalid_storage_path", """{"error":"invalid_storage_path"}""");
                    return;
                }
                ctx.Items[BunnyProxyGate.AccessKeyItem] = zone.Password;
                ctx.Items[StorageUriItem] = uri;
                break;
            }
        }
        await next();
    }

    private async Task<bool> CheckBodyFieldsAsync(HttpContext ctx, IReadOnlyCollection<string> allowed)
    {
        var body = await ReadBodyAsync(ctx);
        // "Name" allows any value, "Name=value" only exactly that value.
        var rules = allowed.Select(a => a.Split('=', 2)).ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : null, StringComparer.OrdinalIgnoreCase);
        var refused = body is null
            ? ["(not a JSON object)"]
            : body.Where(p => !rules.TryGetValue(p.Key, out var only) || (only is not null && p.Value?.ToJsonString() != only))
                .Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        if (refused.Count == 0) return true;
        await Refuse(ctx, 400, "body_field_not_allowed", JsonSerializer.Serialize(new
        {
            error = "body_field_not_allowed",
            fields = refused,
            allowed = allowed.Order(StringComparer.Ordinal),
        }));
        return false;
    }

    /// <summary>The request body as a JSON object, left readable for the forwarder; null when it is not one.</summary>
    private static async Task<JsonObject?> ReadBodyAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength > MaxBodyBytes) return null;
        ctx.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        ctx.Request.Body.Position = 0;
        if (buffer.Length > MaxBodyBytes) return null;
        try
        {
            return JsonNode.Parse(buffer.ToArray()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Name of the pull zone or storage zone with this ID, read upstream; null when there is none.</summary>
    private async Task<string?> ZoneNameAsync(HttpContext ctx, string? ownerKey, string pathPrefix, string? id)
    {
        if (ownerKey is null || id is null || !id.All(char.IsAsciiDigit) || id.Length is 0 or > 18) return null;
        return await GetJsonAsync(ctx, ownerKey, pathPrefix + id) is JsonObject zone ? Text(zone, "Name") : null;
    }

    private async Task<StorageZoneAccess?> StorageZoneAsync(HttpContext ctx, string? ownerKey, string zoneName)
    {
        if (ownerKey is null) return null;
        // Keyed by the owner's key itself: zone names are unique across Bunny, but a cached password must never serve another owner.
        var cacheKey = (nameof(SitePolicy), ownerKey, zoneName.ToLowerInvariant());
        if (cache.TryGetValue(cacheKey, out StorageZoneAccess? cached)) return cached;

        var list = await GetJsonAsync(ctx, ownerKey, "/storagezone?search=" + Uri.EscapeDataString(zoneName)) as JsonArray;
        var summary = list?.OfType<JsonObject>().FirstOrDefault(z => string.Equals(Text(z, "Name"), zoneName, StringComparison.OrdinalIgnoreCase));
        var id = summary is null ? null : Property(summary, "Id")?.ToString();
        // Search results may leave out the password; the zone by ID has it.
        if (id is null || await GetJsonAsync(ctx, ownerKey, "/storagezone/" + id) is not JsonObject zone) return null;
        if (Text(zone, "Password") is not { Length: > 0 } password || Text(zone, "Region") is not { } region
            || !StorageHosts.TryGetValue(region, out var host))
            return null;

        var access = new StorageZoneAccess(password, host);
        cache.Set(cacheKey, access, ZoneCacheLifetime);
        return access;
    }

    private async Task<JsonNode?> GetJsonAsync(HttpContext ctx, string ownerKey, string pathAndQuery)
    {
        var client = clients.CreateClient(new ForwarderHttpClientContext { ClusterId = "bunny" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bunny.net" + pathAndQuery);
        request.Headers.TryAddWithoutValidation("AccessKey", ownerKey);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, ctx.RequestAborted);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ctx.RequestAborted));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    private static string? RouteValue(HttpContext ctx, string name) =>
        ctx.Request.RouteValues.TryGetValue(name, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

    private static JsonNode? Property(JsonObject o, string name) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonObject o, string name) =>
        Property(o, name) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Outside the scope looks the same as absent, so a scoped caller cannot probe other zones.</summary>
    private static Task NotInScope(HttpContext ctx) =>
        Refuse(ctx, 404, "site_not_in_scope", """{"error":"not_found"}""");

    private static async Task Refuse(HttpContext ctx, int status, string reason, string body)
    {
        ctx.Items[ProxyRequestBodyPolicy.LocalReasonItem] = reason;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body);
    }
}
