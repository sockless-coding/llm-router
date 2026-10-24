using System.Text.Json;
using System.Text.Json.Nodes;

namespace LR.Core.Services;

/// <summary>
/// Shrinks a JSON payload for the request log by replacing every long string value — in practice
/// base64 images, videos and preview frames, wherever they sit in the document — with its size.
/// </summary>
public static class LargeValueRedactor
{
    /// <summary>Strings at least this long are replaced; prompts stay readable.</summary>
    public const int Threshold = 4096;

    /// <summary>
    /// <paramref name="json"/> with each long string replaced by <c>&lt;N KB omitted&gt;</c>, or
    /// unchanged if it isn't JSON.
    /// </summary>
    public static string Redact(string json)
    {
        try
        {
            var root = JsonNode.Parse(json);
            if (root is null)
                return json;
            return Redact(root) is { } replaced ? replaced.ToJsonString() : root.ToJsonString();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // Returns a replacement for a long string node; otherwise redacts the node's children in place.
    private static JsonNode? Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is not null && Redact(child) is { } replaced)
                        obj[key] = replaced;
                }
                return null;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child && Redact(child) is { } replaced)
                        array[i] = replaced;
                }
                return null;
            case JsonValue value when value.TryGetValue<string>(out var s) && s.Length >= Threshold:
                return JsonValue.Create($"<{s.Length * 3 / 4 / 1024} KB omitted>");
            default:
                return null;
        }
    }
}
