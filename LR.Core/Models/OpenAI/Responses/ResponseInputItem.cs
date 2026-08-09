using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI.Responses;

/// <summary>
/// The kind of a Responses API input item.
/// </summary>
public enum ResponseInputItemKind
{
    Message,
    FunctionCall,
    FunctionCallOutput,
    Reasoning,

    /// <summary>
    /// A recognized-but-unsupported item type (e.g. "item_reference", built-in tool calls).
    /// Carried through so the caller can produce a clear error instead of silently dropping it.
    /// </summary>
    Unsupported
}

/// <summary>
/// A single item in the Responses API `input` array. Modeled as one class with nullable
/// per-kind fields (matching this codebase's existing manual-converter style, e.g.
/// <see cref="ChatMessageContentConverter"/>) rather than JSON polymorphism.
/// </summary>
public class ResponseInputItem
{
    public ResponseInputItemKind Kind { get; set; } = ResponseInputItemKind.Message;

    /// <summary>The raw "type" string as received, kept for diagnostics on unsupported items.</summary>
    public string? RawType { get; set; }

    // --- message kind ---
    public string Role { get; set; } = "user";
    public ResponseInputContent? Content { get; set; }

    // --- function_call kind (echoed assistant tool call) and function_call_output kind ---
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
    public string? Output { get; set; }

    public string? Id { get; set; }
}

/// <summary>
/// Polymorphic content for a Responses API input message item: plain text or an array of parts.
/// </summary>
public class ResponseInputContent
{
    private ResponseInputContent() { }

    public string? Text { get; private set; }
    public List<ResponseInputContentPart>? Parts { get; private set; }

    public static ResponseInputContent FromText(string text) => new() { Text = text };
    public static ResponseInputContent FromParts(List<ResponseInputContentPart> parts) => new() { Parts = parts };
}

/// <summary>
/// A single part of a Responses API input message: "input_text", "input_image", "input_file"
/// (user-provided), or "output_text"/"refusal" (echoed assistant turns from a prior response).
/// </summary>
public class ResponseInputContentPart
{
    public string Type { get; set; } = "input_text";
    public string? Text { get; set; }
    public string? ImageUrl { get; set; }
    public string? Detail { get; set; }
    public string? FileId { get; set; }
    public string? FileUrl { get; set; }
    public string? Filename { get; set; }
    public string? FileData { get; set; }
}

/// <summary>
/// Deserializes the Responses API `input` field, which is either a bare string (shorthand for a
/// single user message) or an array of typed input items.
/// </summary>
public class ResponseInputListConverter : JsonConverter<List<ResponseInputItem>>
{
    public override List<ResponseInputItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new List<ResponseInputItem>();

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString() ?? string.Empty;
            return new List<ResponseInputItem>
            {
                new() { Kind = ResponseInputItemKind.Message, Role = "user", Content = ResponseInputContent.FromText(text) }
            };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var items = new List<ResponseInputItem>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var item = ReadItem(ref reader);
                if (item is not null) items.Add(item);
            }
            return items;
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} for Responses `input`");
    }

    private static ResponseInputItem ReadItem(ref Utf8JsonReader reader)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = GetString(root, "type");

        var item = new ResponseInputItem { RawType = type, Id = GetString(root, "id") };

        switch (type)
        {
            case "function_call":
                item.Kind = ResponseInputItemKind.FunctionCall;
                item.CallId = GetString(root, "call_id");
                item.Name = GetString(root, "name");
                item.Arguments = GetString(root, "arguments");
                return item;

            case "function_call_output":
                item.Kind = ResponseInputItemKind.FunctionCallOutput;
                item.CallId = GetString(root, "call_id");
                item.Output = root.TryGetProperty("output", out var outEl)
                    ? (outEl.ValueKind == JsonValueKind.String ? outEl.GetString() : outEl.GetRawText())
                    : null;
                return item;

            case "reasoning":
                item.Kind = ResponseInputItemKind.Reasoning;
                return item;

            case "message":
            case null:
                item.Kind = ResponseInputItemKind.Message;
                item.Role = GetString(root, "role") ?? "user";
                if (root.TryGetProperty("content", out var contentEl))
                    item.Content = ParseContent(contentEl);
                return item;

            default:
                // item_reference, built-in tool call/output items, computer_call, etc. — not
                // supported by a llama.cpp backend. Surfaced as Unsupported so the handler can
                // return a clear 400 instead of silently dropping conversation state.
                item.Kind = ResponseInputItemKind.Unsupported;
                return item;
        }
    }

    private static ResponseInputContent ParseContent(JsonElement contentEl)
    {
        if (contentEl.ValueKind == JsonValueKind.String)
            return ResponseInputContent.FromText(contentEl.GetString() ?? string.Empty);

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<ResponseInputContentPart>();
            foreach (var partEl in contentEl.EnumerateArray())
            {
                var part = new ResponseInputContentPart
                {
                    Type = GetString(partEl, "type") ?? "input_text",
                    Text = GetString(partEl, "text"),
                    Detail = GetString(partEl, "detail"),
                    FileId = GetString(partEl, "file_id"),
                    FileUrl = GetString(partEl, "file_url"),
                    Filename = GetString(partEl, "filename"),
                    FileData = GetString(partEl, "file_data")
                };
                // Responses API sends the image URL as a bare string field, not a nested object
                // like Chat Completions' image_url.url.
                part.ImageUrl = GetString(partEl, "image_url");
                parts.Add(part);
            }
            return ResponseInputContent.FromParts(parts);
        }

        return ResponseInputContent.FromText(string.Empty);
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public override void Write(Utf8JsonWriter writer, List<ResponseInputItem> value, JsonSerializerOptions options)
    {
        // Only used when re-serializing a request for logging; write the array form.
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStartObject();
            switch (item.Kind)
            {
                case ResponseInputItemKind.FunctionCall:
                    writer.WriteString("type", "function_call");
                    writer.WriteString("call_id", item.CallId);
                    writer.WriteString("name", item.Name);
                    writer.WriteString("arguments", item.Arguments);
                    break;
                case ResponseInputItemKind.FunctionCallOutput:
                    writer.WriteString("type", "function_call_output");
                    writer.WriteString("call_id", item.CallId);
                    writer.WriteString("output", item.Output);
                    break;
                default:
                    writer.WriteString("type", "message");
                    writer.WriteString("role", item.Role);
                    if (item.Content?.Text is not null)
                        writer.WriteString("content", item.Content.Text);
                    break;
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
