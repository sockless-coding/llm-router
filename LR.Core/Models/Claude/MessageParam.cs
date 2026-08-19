using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Core.Models.Claude;

/// <summary>
/// A single message in a Claude conversation.
/// </summary>
public class MessageParam
{
    /// <summary>
    /// The role of the messages author (user or assistant).
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>
    /// The contents of the message. Can be a plain string or an array of content
    /// blocks (text, image, tool_use, tool_result, thinking, etc).
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(MessageContentConverter))]
    public MessageContent Content { get; set; } = MessageContent.FromText(string.Empty);
}

/// <summary>
/// Polymorphic content for a Claude message or system prompt. Anthropic's Messages API accepts
/// either a plain string or an array of content blocks. Real clients (Claude Code, the Claude
/// SDKs) routinely send the array form — for multi-turn tool use, images, and cached system
/// prompts — so both shapes must round-trip without loss. Individual blocks are kept as raw
/// JSON rather than modeled field-by-field: this router forwards message content to the backend
/// verbatim and never inspects it, so preserving the original JSON exactly (including block
/// types and fields this router doesn't otherwise know about, like tool_use/tool_result/
/// cache_control/signature) is both simpler and safer than reimplementing Anthropic's growing
/// set of content block schemas.
/// </summary>
public class MessageContent
{
    private MessageContent() { }

    /// <summary>Plain text content.</summary>
    public string? Text { get; private set; }

    /// <summary>Array of content blocks, preserved as raw JSON.</summary>
    public List<JsonElement>? Blocks { get; private set; }

    /// <summary>Create content from a plain text string.</summary>
    public static MessageContent FromText(string text) => new() { Text = text };

    /// <summary>Create content from an array of raw content blocks.</summary>
    public static MessageContent FromBlocks(List<JsonElement> blocks) => new() { Blocks = blocks };
}

/// <summary>
/// Custom JSON converter for polymorphic MessageContent. Handles: string (plain text) or
/// array (content blocks, preserved as raw JsonElements).
/// </summary>
public class MessageContentConverter : JsonConverter<MessageContent>
{
    public override MessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return MessageContent.FromText(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var blocks = doc.RootElement.EnumerateArray().Select(b => b.Clone()).ToList();
            return MessageContent.FromBlocks(blocks);
        }

        throw new JsonException($"Unexpected token type {reader.TokenType} for message content");
    }

    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        if (value.Blocks is not null)
        {
            writer.WriteStartArray();
            foreach (var block in value.Blocks) block.WriteTo(writer);
            writer.WriteEndArray();
            return;
        }

        writer.WriteStringValue(value.Text ?? string.Empty);
    }
}
