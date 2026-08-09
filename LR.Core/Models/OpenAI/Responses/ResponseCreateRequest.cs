using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI.Responses;

/// <summary>
/// Request body for the OpenAI Responses API (POST /v1/responses).
/// </summary>
public class ResponseCreateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Either a bare string (shorthand user message) or an array of typed input items.</summary>
    [JsonPropertyName("input")]
    [JsonConverter(typeof(ResponseInputListConverter))]
    public List<ResponseInputItem> Input { get; set; } = new();

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; set; }

    /// <summary>Whether to persist the response for retrieval and previous_response_id chaining. Defaults to true.</summary>
    [JsonPropertyName("store")]
    public bool? Store { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    /// <summary>Process asynchronously: return immediately with status "queued" and let the caller poll/cancel.</summary>
    [JsonPropertyName("background")]
    public bool Background { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("max_output_tokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool? ParallelToolCalls { get; set; }

    /// <summary>"none", "auto", "required", or an object naming a specific function — same shape as Chat Completions.</summary>
    [JsonPropertyName("tool_choice")]
    public ChatToolChoice? ToolChoice { get; set; }

    /// <summary>Only "function" tools are supported (see <see cref="ResponseTool"/>).</summary>
    [JsonPropertyName("tools")]
    public List<ResponseTool>? Tools { get; set; }

    [JsonPropertyName("text")]
    public ResponseTextOptions? Text { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("truncation")]
    public string? Truncation { get; set; }

    [JsonPropertyName("reasoning")]
    public ResponseReasoningOptions? Reasoning { get; set; }
}

/// <summary>Output formatting options — mirrors Chat Completions' response_format.</summary>
public class ResponseTextOptions
{
    [JsonPropertyName("format")]
    public ChatResponseFormat? Format { get; set; }
}

/// <summary>
/// Reasoning options. Only "effort" is passed through (as a best-effort hint some llama.cpp
/// forks understand); reasoning "summary" synthesis is out of scope — only raw
/// reasoning_content the backend itself returns is surfaced.
/// </summary>
public class ResponseReasoningOptions
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }
}
