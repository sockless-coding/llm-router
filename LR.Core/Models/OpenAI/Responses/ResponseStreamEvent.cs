using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI.Responses;

/// <summary>
/// A single Responses API streaming SSE event. One class covers the whole event vocabulary
/// (response.created/in_progress/completed/failed/incomplete, response.output_item.added/done,
/// response.content_part.added, response.output_text.delta/done, response.reasoning_text.delta/done,
/// response.function_call_arguments.delta/done) — only the fields relevant to <see cref="Type"/>
/// are populated, the rest serialize as omitted via WhenWritingNull.
/// Framed on the wire as "event: {Type}\ndata: {json}\n\n" (named-event SSE, like the real
/// Responses API and this codebase's ClaudeHandler — NOT Chat Completions' data-only framing).
/// </summary>
public class ResponseStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("sequence_number")]
    public int SequenceNumber { get; set; }

    /// <summary>Full response snapshot — used by response.created/in_progress/completed/failed/incomplete.</summary>
    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseObject? Response { get; set; }

    [JsonPropertyName("output_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputIndex { get; set; }

    [JsonPropertyName("item_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemId { get; set; }

    [JsonPropertyName("content_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContentIndex { get; set; }

    /// <summary>Used by response.output_item.added/done.</summary>
    [JsonPropertyName("item")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseOutputItem? Item { get; set; }

    /// <summary>Used by response.content_part.added.</summary>
    [JsonPropertyName("part")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseOutputContentPart? Part { get; set; }

    /// <summary>Incremental text — used by output_text.delta, reasoning_text.delta, function_call_arguments.delta.</summary>
    [JsonPropertyName("delta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Delta { get; set; }

    /// <summary>Final accumulated text — used by output_text.done, reasoning_text.done.</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    /// <summary>Final accumulated arguments — used by function_call_arguments.done.</summary>
    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }
}
