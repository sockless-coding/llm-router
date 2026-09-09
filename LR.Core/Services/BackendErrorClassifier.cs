namespace LR.Core.Services;

/// <summary>
/// Recognizes backend failure conditions that are expected/user-actionable (as opposed to bugs)
/// so callers can respond with a clear, well-formed error instead of an unhandled-exception log
/// and an abruptly killed connection.
/// </summary>
public static class BackendErrorClassifier
{
    /// <summary>
    /// True if <paramref name="ex"/> (or any exception it wraps) is llama.cpp's
    /// "Context size has been exceeded." server error — the prompt plus requested generation
    /// no longer fits in the server's context window.
    /// </summary>
    public static bool IsContextExceeded(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("context size", StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains("exceed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// A message safe and useful to hand back to the client for a failed backend request —
    /// actionable wording for recognized conditions, the raw message otherwise.
    /// </summary>
    public static string ToClientMessage(Exception ex) => IsContextExceeded(ex)
        ? "The request exceeds the model's context window. Shorten the conversation or prompt, lower max_tokens, or switch to a preset with a larger context size."
        : $"The backend request failed: {ex.Message}";
}
