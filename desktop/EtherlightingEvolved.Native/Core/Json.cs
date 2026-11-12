using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// Reading workspace JSON the way the browser workspace does: missing or mistyped fields read as absent rather than
/// failing. Paths are dot separated ("link.speed_mbps"); a numeric segment indexes an array.
/// </summary>
public static class Json
{
    public static JsonNode? At(this JsonNode? node, string path)
    {
        if (node is null) return null;
        foreach (var segment in path.Split('.'))
        {
            node = node switch
            {
                JsonObject obj => obj.TryGetPropertyValue(segment, out var value) ? value : null,
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    public static string? Str(this JsonNode? node, string? path = null)
    {
        var value = path is null ? node : node.At(path);
        return value is JsonValue json && json.GetValueKind() == JsonValueKind.String ? json.GetValue<string>() : null;
    }

    public static double? Num(this JsonNode? node, string? path = null)
    {
        var value = path is null ? node : node.At(path);
        return Model.Js.Num(value) is { } number && double.IsFinite(number) ? number : null;
    }

    public static int? Int(this JsonNode? node, string? path = null) => node.Num(path) is { } number && number == Math.Floor(number) ? (int)number : null;

    public static bool? Bool(this JsonNode? node, string? path = null)
    {
        var value = path is null ? node : node.At(path);
        return value is JsonValue json && json.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? json.GetValue<bool>() : null;
    }

    /// <summary>JavaScript truthiness, for porting conditions such as <c>if (value)</c>.</summary>
    public static bool Truthy(this JsonNode? node, string? path = null)
    {
        var value = path is null ? node : node.At(path);
        if (value is null) return false;
        if (value is not JsonValue json) return true;
        return json.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => json.GetValue<string>().Length > 0,
            JsonValueKind.Number => json.Num() is { } number && number != 0,
            _ => true,
        };
    }

    public static JsonArray Arr(this JsonNode? node, string? path = null) => (path is null ? node : node.At(path)) as JsonArray ?? [];

    public static JsonObject Obj(this JsonNode? node, string? path = null) => (path is null ? node : node.At(path)) as JsonObject ?? [];

    public static IEnumerable<JsonObject> Objects(this JsonNode? node, string? path = null) => node.Arr(path).OfType<JsonObject>();

    public static JsonNode? Copy(this JsonNode? node) => node?.DeepClone();

    public static string Text(this JsonNode? node) => node?.ToJsonString() ?? "null";

    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    public static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }
}
