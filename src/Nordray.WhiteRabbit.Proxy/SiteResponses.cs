using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nordray.WhiteRabbit.Proxy;

/// <summary>
/// Filters a pull zone or storage zone list to the zones a caller may see. The API answers with a plain array, or
/// for some pull zone queries with an <c>{ Items, HasMoreItems }</c> envelope; both are handled.
/// </summary>
public static class SiteResponses
{
    /// <summary>The list without the zones <paramref name="keep"/> rejects (by Name), or null when it is not a list.</summary>
    public static string? Filter(string json, Func<string?, bool> keep)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        var items = root switch
        {
            JsonArray array => array,
            JsonObject envelope => envelope.FirstOrDefault(p => string.Equals(p.Key, "Items", StringComparison.OrdinalIgnoreCase)).Value as JsonArray,
            _ => null,
        };
        if (items is null) return null;

        for (var i = items.Count - 1; i >= 0; i--)
        {
            var name = items[i] is JsonObject zone
                && zone.FirstOrDefault(p => string.Equals(p.Key, "Name", StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
                && v.GetValueKind() == JsonValueKind.String
                    ? v.GetValue<string>()
                    : null;
            if (!keep(name)) items.RemoveAt(i);
        }
        return root!.ToJsonString();
    }
}
