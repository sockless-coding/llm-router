using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI.Responses;

/// <summary>
/// The Responses API response object — returned from create, retrieve, and (as the terminal
/// SSE event payload) streaming.
/// </summary>
public class ResponseObject
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"resp_{Guid.NewGuid():N}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "response";

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>"queued" | "in_progress" | "completed" | "failed" | "incomplete" | "cancelled".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "in_progress";

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseError? Error { get; set; }

    [JsonPropertyName("incomplete_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseIncompleteDetails? IncompleteDetails { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("output")]
    public List<ResponseOutputItem> Output { get; set; } = new();

    /// <summary>SDK convenience field: concatenation of all output_text parts across message items.</summary>
    [JsonPropertyName("output_text")]
    public string OutputText => string.Join(string.Empty, Output
        .Where(o => o.Type == "message" && o.Content is not null)
        .SelectMany(o => o.Content!)
        .Where(c => c.Type == "output_text")
        .Select(c => c.Text ?? string.Empty));

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseUsage? Usage { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool ParallelToolCalls { get; set; } = true;

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }

    [JsonPropertyName("store")]
    public bool Store { get; set; } = true;

    [JsonPropertyName("background")]
    public bool Background { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChatToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("tools")]
    public List<ResponseTool> Tools { get; set; } = new();

    [JsonPropertyName("truncation")]
    public string? Truncation { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

public class ResponseError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class ResponseIncompleteDetails
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public class ResponseUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}
