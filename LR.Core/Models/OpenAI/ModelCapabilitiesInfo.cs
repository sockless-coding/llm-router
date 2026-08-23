using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// Model info returned by the /v1/models/capabilities endpoint. Extends the plain OpenAI
/// <see cref="ModelInfo"/> shape with the fields a client needs to configure itself against a
/// model without any manual input — context size, output budget, and modality/tool support —
/// so consumers like the VS Code Copilot provider can auto-populate their model list.
/// </summary>
public class ModelCapabilitiesInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Total context window in tokens (prompt + completion).</summary>
    [JsonPropertyName("context_length")]
    public int ContextLength { get; set; }

    /// <summary>Best-effort budget for how many tokens a single completion may produce.</summary>
    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; set; }

    /// <summary>Whether this preset is configured with a multimodal projector (image input).</summary>
    [JsonPropertyName("vision")]
    public bool Vision { get; set; }

    /// <summary>Whether this preset is expected to support OpenAI-style tool/function calling.</summary>
    [JsonPropertyName("tool_calling")]
    public bool ToolCalling { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }

    [JsonPropertyName("quantization")]
    public string? Quantization { get; set; }

    /// <summary>
    /// Whether this preset's chat template reads a reasoning-effort-style variable (e.g.
    /// "reasoning_effort", "thinking_budget"), meaning a client can steer reasoning depth
    /// per-request via the <c>reasoning_effort</c> field on chat completions.
    /// </summary>
    [JsonPropertyName("supports_reasoning_effort")]
    public bool SupportsReasoningEffort { get; set; }

    /// <summary>
    /// The reasoning effort this preset is launched with by default (its <c>--reasoning-effort</c>
    /// server arg), if configured. A client-supplied <c>reasoning_effort</c> on a request
    /// overrides this for that request only.
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// The discrete effort levels the chat template itself checks for (e.g. "low", "medium",
    /// "high", "xhigh"), heuristically extracted from the template's own comparisons/defaults.
    /// Empty when <see cref="SupportsReasoningEffort"/> is false, or when it's true but the
    /// template doesn't gate on a fixed set of values a client can pick from.
    /// </summary>
    [JsonPropertyName("reasoning_effort_options")]
    public IReadOnlyList<string> ReasoningEffortOptions { get; set; } = [];
}
