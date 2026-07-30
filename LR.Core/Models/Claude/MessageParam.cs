namespace LR.Core.Models.Claude;

/// <summary>
/// A single message in a Claude conversation.
/// </summary>
public class MessageParam
{
    /// <summary>
    /// The role of the messages author (user or assistant).
    /// </summary>
    public string Role { get; set; } = "user";

    /// <summary>
    /// The contents of the message.
    /// </summary>
    public string Content { get; set; } = string.Empty;
}
