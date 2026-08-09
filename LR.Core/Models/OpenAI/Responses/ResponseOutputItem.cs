using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI.Responses;

/// <summary>
/// A single item in a Responses API `output` array: an assistant message, a function call the
/// model wants executed, or a reasoning block.
/// </summary>
public class ResponseOutputItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>"message" | "function_call" | "reasoning".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    // --- message ---
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResponseOutputContentPart>? Content { get; set; }

    // --- function_call ---
    [JsonPropertyName("call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallId { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }
}

/// <summary>
/// A content part of a "message" or "reasoning" output item.
/// Type is "output_text", "refusal", or "reasoning_text".
/// </summary>
public class ResponseOutputContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "output_text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("annotations")]
    public List<object> Annotations { get; set; } = new();
}
