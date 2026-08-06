using System.Text.Json.Serialization;

namespace LR.Core.Models.OpenAI;

/// <summary>
/// The role of the author of a chat message.
/// </summary>
public enum ChatMessageRole
{
    /// <summary>System message providing instructions or context.</summary>
    [JsonPropertyName("system")]
    System,

    /// <summary>User message containing input for the model.</summary>
    [JsonPropertyName("user")]
    User,

    /// <summary>Assistant (model) response message.</summary>
    [JsonPropertyName("assistant")]
    Assistant,

    /// <summary>Tool output message providing results from a tool call.</summary>
    [JsonPropertyName("tool")]
    Tool,

    /// <summary>(Deprecated) Function call result message. Use <see cref="Tool"/> instead.</summary>
    [Obsolete("Use Tool instead.")]
    [JsonPropertyName("function")] 
    Function,

    /// <summary>Developer message for additional system-level instructions (newer models).</summary>
    [JsonPropertyName("developer")]
    Developer
}
