using System.Text.Json.Serialization;

namespace LR.Core.Models.Claude;

/// <summary>
/// SSE event types for Claude streaming responses.
/// </summary>
public class MessageStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The event data payload (varies by type).
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// message_start event — contains the message ID, model, and usage.
/// </summary>
public class MessageStartData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message_start";

    [JsonPropertyName("message")]
    public MessageEnvelope Message { get; set; } = new();
}

public class MessageEnvelope
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public List<object> Content { get; set; } = new();

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public object? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    public Usage Usage { get; set; } = new();
}

/// <summary>
/// content_block_stop event — closes the content block opened by content_block_start.
/// Real Anthropic clients (including the official SDK) expect this before message_delta/
/// message_stop; omitting it produces a malformed event sequence some clients reject outright.
/// </summary>
public class ContentBlockStopData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "content_block_stop";

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

/// <summary>
/// content_block_start event — a new content block is beginning.
/// </summary>
public class ContentBlockStartData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "content_block_start";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("content_block")]
    public ContentBlock ContentBlock { get; set; } = new();
}

/// <summary>
/// content_block_delta event — a delta of text content.
/// </summary>
public class ContentBlockDeltaData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "content_block_delta";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public DeltaContentBlockDelta Delta { get; set; } = new();
}

public class DeltaContentBlockDelta
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text_delta";

    /// <summary>
    /// Text content for text_delta type.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Thinking/reasoning content for thinking_delta type.
    /// Used when the model has reasoning capabilities enabled.
    /// </summary>
    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    /// <summary>
    /// Incremental fragment of a tool call's JSON-encoded arguments, for input_json_delta type.
    /// Fragments for a given content block are concatenated in order to form the full arguments
    /// JSON string once the block closes.
    /// </summary>
    [JsonPropertyName("partial_json")]
    public string? PartialJson { get; set; }
}

/// <summary>
/// message_delta event — final usage and stop reason.
/// </summary>
public class MessageDeltaData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message_delta";

    [JsonPropertyName("delta")]
    public DeltaMessageDelta Delta { get; set; } = new();

    [JsonPropertyName("usage")]
    public Usage? Usage { get; set; }
}

public class DeltaMessageDelta
{
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public object? StopSequence { get; set; }
}

/// <summary>
/// message_stop event — end of streaming.
/// </summary>
public class MessageStopData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message_stop";
}

