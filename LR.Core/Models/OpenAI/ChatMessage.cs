namespace LR.Core.Models.OpenAI;

/// <summary>
/// A single message in a chat conversation.
/// </summary>
public class ChatMessage
{
    /// <summary>
    /// The role of the messages author (system, user, assistant).
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// The contents of the message.
    /// </summary>
    public string? Content { get; set; }
}
