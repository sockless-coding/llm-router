namespace LR.Core.Models;

/// <summary>
/// API protocol supported by the gateway.
/// </summary>
public enum ApiProtocol
{
    /// <summary>
    /// OpenAI-compatible chat completions API (e.g., /v1/chat/completions, /v1/models).
    /// </summary>
    OpenAI = 0,

    /// <summary>
    /// Claude Messages API (e.g., /v1/messages).
    /// </summary>
    Claude = 1,

    /// <summary>
    /// Ollama API (e.g., /api/chat, /api/tags).
    /// </summary>
    Ollama = 2,
}
