using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// The reason the model stopped generating tokens.
/// </summary>
public enum FinishReason
{
    /// <summary>The model hit a natural stop point or a provided stop sequence.</summary>
    [JsonPropertyName("stop")]
    Stop,

    /// <summary>The maximum number of tokens specified in the request was reached.</summary>
    [JsonPropertyName("length")]
    Length,

    /// <summary>Content was omitted due to a triggered content filter rule.</summary>
    [JsonPropertyName("content_filter")]
    ContentFilter,

    /// <summary>The model called one or more tools that were defined in the request.</summary>
    [JsonPropertyName("tool_calls")]
    ToolCalls,

    /// <summary>(Deprecated) The model called a function. Use <see cref="ToolCalls"/> instead.</summary>
    [Obsolete("Use ToolCalls instead.")]
    [JsonPropertyName("function_call")]
    FunctionCall
}
