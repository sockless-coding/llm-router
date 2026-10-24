namespace LR.Core.Models;

/// <summary>
/// What a server engine generates. Decides which API endpoints its presets serve — a text engine's
/// presets answer chat/completions/embeddings, an image engine's answer <c>/v1/images/*</c> — so the
/// router never hands a chat request to a diffusion server or an image request to an LLM.
/// </summary>
public enum EngineModality
{
    Text = 0,
    Image = 1,
}
