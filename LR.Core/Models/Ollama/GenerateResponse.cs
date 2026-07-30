using System.Text.Json.Serialization;

namespace LR.Core.Models.Ollama;

/// <summary>
/// Response for Ollama /api/generate endpoint.
/// </summary>
public class GenerateResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// The generated text (or empty if streaming).
    /// </summary>
    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    /// <summary>
    /// Reason why generation stopped (e.g., "stop", "unload").
    /// </summary>
    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; set; }

    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }

    [JsonPropertyName("load_duration")]
    public long LoadDuration { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int PromptEvalCount { get; set; }

    [JsonPropertyName("prompt_eval_duration")]
    public long PromptEvalDuration { get; set; }

    [JsonPropertyName("eval_count")]
    public int EvalCount { get; set; }

    [JsonPropertyName("eval_duration")]
    public long EvalDuration { get; set; }
}

