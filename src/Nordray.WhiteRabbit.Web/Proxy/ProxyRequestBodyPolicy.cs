using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nordray.WhiteRabbit.Proxy;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Nordray.WhiteRabbit.Web.Proxy;

/// <summary>
/// Checks and adjusts a request body after the gate has authorised the request, before it is forwarded:
/// <list type="bullet">
/// <item><b>Allowed fields.</b> Operations with <c>AllowedBodyFields</c> accept only a JSON object with those
/// top-level properties, e.g. a container PATCH may change the image but not environment variables, endpoints or
/// volumes.</item>
/// <item><b>Redacted environment values.</b> Tools such as <c>bunny apps env set</c> read a container's variables,
/// change one and send them all back. White Rabbit shows them the values as <see cref="JsonRedaction.Placeholder"/>,
/// so every value equal to the placeholder is replaced with the variable's current value, read upstream with the
/// owner's key. The caller never sees the values, and writing a placeholder back can never overwrite a secret.</item>
/// <item><b>Redacted app environment.</b> The same for a whole app (<c>PUT /mc/apps/{appId}</c>): each container's
/// placeholders are restored from the current app's container with the same <c>id</c>, or else the same <c>name</c>.
/// A placeholder that cannot be restored, or one anywhere else in the body, is refused.</item>
/// </list>
/// A refusal sets <see cref="LocalReasonItem"/>, so the audit record says White Rabbit answered, not Bunny.
/// </summary>
public sealed class ProxyRequestBodyPolicy(IForwarderHttpClientFactory clients)
{
    public const string RestoreMetadataKey = "RestoreRedactedEnvironmentFrom";
    public const string AllowedFieldsMetadataKey = "AllowedBodyFields";
    public const string RestoreAppMetadataKey = "RestoreRedactedAppEnvironmentFrom";

    /// <summary>Why White Rabbit answered a request itself, for the audit record.</summary>
    public const string LocalReasonItem = "bunny.local-reason";

    /// <summary>Bunny's status when White Rabbit replaced Bunny's answer (e.g. an unredactable body).</summary>
    public const string UpstreamStatusItem = "bunny.upstream-status";

    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>Runs <paramref name="next"/> with the body checked and restored, or writes an error and stops.</summary>
    public async Task InvokeAsync(HttpContext ctx, RouteModel route, Func<Task> next)
    {
        var metadata = route.Config.Metadata;
        var allowed = metadata?.GetValueOrDefault(AllowedFieldsMetadataKey) is { Length: > 0 } list
            ? list.Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var restoreFrom = metadata?.GetValueOrDefault(RestoreMetadataKey);
        var restoreAppFrom = metadata?.GetValueOrDefault(RestoreAppMetadataKey);
        if (allowed is null && string.IsNullOrEmpty(restoreFrom) && string.IsNullOrEmpty(restoreAppFrom))
        {
            await next();
            return;
        }

        var (body, tooLarge) = await ReadObjectAsync(ctx);
        if (body is null)
        {
            await Refuse(ctx, tooLarge ? 413 : 400, tooLarge ? "body_too_large" : "invalid_body",
                tooLarge ? """{"error":"body_too_large"}""" : """{"error":"invalid_body","detail":"Expected a JSON object."}""");
            return;
        }

        if (allowed is not null)
        {
            // Bunny matches property names case-insensitively, so the check does too. A field that is present is
            // refused even when null or empty: an empty environment list would still remove every variable.
            var refused = body.Select(p => p.Key).Where(k => !allowed.Contains(k)).Order(StringComparer.Ordinal).ToList();
            if (refused.Count > 0)
            {
                await Refuse(ctx, 400, "body_field_not_allowed", JsonSerializer.Serialize(new
                {
                    error = "body_field_not_allowed",
                    fields = refused,
                    allowed = allowed.Order(StringComparer.Ordinal),
                }));
                return;
            }
        }

        if (!string.IsNullOrEmpty(restoreFrom) && !await RestoreAsync(ctx, body, restoreFrom))
            return;
        if (!string.IsNullOrEmpty(restoreAppFrom) && !await RestoreAppAsync(ctx, body, restoreAppFrom))
            return;

        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        await next();
    }

    /// <summary>Restores placeholders in an environment object; false when it wrote a refusal.</summary>
    private async Task<bool> RestoreAsync(HttpContext ctx, JsonObject body, string sourceTemplate)
    {
        foreach (var (_, value) in body)
        {
            if (value is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
            {
                await Refuse(ctx, 400, "invalid_environment",
                    """{"error":"invalid_environment","detail":"Expected a JSON object of variable names to string values."}""");
                return false;
            }
        }

        var redacted = body.Where(p => p.Value!.GetValue<string>() == JsonRedaction.Placeholder).Select(p => p.Key).ToList();
        if (redacted.Count == 0) return true;

        var current = await ReadCurrentAsync(ctx, sourceTemplate) is { } container ? Environment(container) : null;
        if (current is null)
        {
            await Refuse(ctx, 502, "current_environment_unavailable", """{"error":"current_environment_unavailable"}""");
            return false;
        }
        foreach (var name in redacted)
        {
            if (!current.TryGetValue(name, out var value))
            {
                await Refuse(ctx, 400, "redacted_value_unknown",
                    """{"error":"redacted_value_unknown","detail":"A variable sent as the redaction placeholder does not exist on the container. Send its real value."}""");
                return false;
            }
            body[name] = value;
        }
        return true;
    }

    /// <summary>Restores placeholders in an app's container environments; false when it wrote a refusal.</summary>
    private async Task<bool> RestoreAppAsync(HttpContext ctx, JsonObject body, string sourceTemplate)
    {
        if (!ContainsPlaceholder(body)) return true;

        var current = await ReadCurrentAsync(ctx, sourceTemplate);
        if (current is null)
        {
            await Refuse(ctx, 502, "current_environment_unavailable", """{"error":"current_environment_unavailable"}""");
            return false;
        }
        var currentContainers = (Property(current, "containerTemplates") as JsonArray ?? []).OfType<JsonObject>().ToList();

        // Bunny matches property names case-insensitively, so this does too.
        foreach (var container in (Property(body, "containerTemplates") as JsonArray ?? []).OfType<JsonObject>())
        {
            var variables = (Property(container, "environmentVariables") as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (!variables.Any(ContainsPlaceholder)) continue;

            var key = Text(container, "id") is { } id ? ("id", id) : ("name", Text(container, "name"));
            var match = key.Item2 is null ? null : currentContainers.FirstOrDefault(c => Text(c, key.Item1) == key.Item2);
            var values = match is null ? [] : Environment(match);
            foreach (var variable in variables)
            {
                var valueKey = variable.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, "value", StringComparison.OrdinalIgnoreCase));
                if (valueKey is null || Text(variable, "value") != JsonRedaction.Placeholder) continue;
                if (Text(variable, "name") is { } name && values.TryGetValue(name, out var value))
                    variable[valueKey] = value;
            }
        }

        if (ContainsPlaceholder(body))
        {
            await Refuse(ctx, 400, "redacted_value_unknown",
                """{"error":"redacted_value_unknown","detail":"A value sent as the redaction placeholder is not a current environment variable of a container with the same id or name. Send its real value."}""");
            return false;
        }
        return true;
    }

    private static bool ContainsPlaceholder(JsonNode? node) => node switch
    {
        JsonObject o => o.Any(p => ContainsPlaceholder(p.Value)),
        JsonArray a => a.Any(ContainsPlaceholder),
        JsonValue v => v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == JsonRedaction.Placeholder,
        _ => false,
    };

    private static JsonNode? Property(JsonObject o, string name) =>
        o.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonObject o, string name) =>
        Property(o, name) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A container's variables by name.</summary>
    private static Dictionary<string, string> Environment(JsonObject container)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in (Property(container, "environmentVariables") as JsonArray ?? []).OfType<JsonObject>())
            if (Text(item, "name") is { } name && Text(item, "value") is { } value) result[name] = value;
        return result;
    }

    /// <summary>The body as a JSON object; null (and whether it was too large) when it is not one.</summary>
    private static async Task<(JsonObject?, bool TooLarge)> ReadObjectAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength > MaxBodyBytes) return (null, true);
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        if (buffer.Length > MaxBodyBytes) return (null, true);
        try
        {
            return (JsonNode.Parse(buffer.ToArray()) as JsonObject, false);
        }
        catch (JsonException)
        {
            return (null, false);
        }
    }

    /// <summary>The current object at the upstream path (a container or an app), or null when it cannot be read.</summary>
    private async Task<JsonObject?> ReadCurrentAsync(HttpContext ctx, string template)
    {
        if (ctx.Items[BunnyProxyGate.AccessKeyItem] is not string apiKey) return null;

        var path = template;
        foreach (var (name, value) in ctx.Request.RouteValues)
            path = path.Replace("{" + name + "}", Uri.EscapeDataString(value?.ToString() ?? ""), StringComparison.Ordinal);
        if (path.Contains('{')) return null;

        var client = clients.CreateClient(new ForwarderHttpClientContext { ClusterId = "bunny" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bunny.net" + path);
        request.Headers.TryAddWithoutValidation("AccessKey", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, ctx.RequestAborted);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ctx.RequestAborted)) as JsonObject;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task Refuse(HttpContext ctx, int status, string reason, string body)
    {
        ctx.Items[LocalReasonItem] = reason;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body);
    }
}
