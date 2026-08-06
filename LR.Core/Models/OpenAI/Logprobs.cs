using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// Log probability information for a chat completion choice.
/// </summary>
public class ChoiceLogprobs
{
    /// <summary>A list of message content tokens with log probability information.</summary>
    [JsonPropertyName("content")]
    public List<ChatTokenLogprob>? Content { get; set; }

    /// <summary>A list of message refusal tokens with log probability information.</summary>
    [JsonPropertyName("refusal")]
    public List<ChatTokenLogprob>? Refusal { get; set; }
}

/// <summary>
/// Log probability details for a single token.
/// </summary>
public class ChatTokenLogprob
{
    /// <summary>The token.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The log probability of this token, if above the logprobs threshold.</summary>
    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    /// <summary>A list of integers representing the UTF-8 bytes representation of the token.</summary>
    [JsonPropertyName("bytes")]
    public List<int>? Bytes { get; set; }

    /// <summary>List of the most likely tokens and their log probabilities at this token position.</summary>
    [JsonPropertyName("top_logprobs")]
    public List<ChatTopLogprob>? TopLogprobs { get; set; }
}

/// <summary>
/// A top candidate token with its log probability.
/// </summary>
public class ChatTopLogprob
{
    /// <summary>The token.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The log probability of this token.</summary>
    [JsonPropertyName("logprob")]
    public double Logprob { get; set; }

    /// <summary>A list of integers representing the UTF-8 bytes representation of the token.</summary>
    [JsonPropertyName("bytes")]
    public List<int>? Bytes { get; set; }
}
