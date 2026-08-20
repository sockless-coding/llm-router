using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Extracts "free" (non-standard) variables referenced by a raw Jinja2 chat-template string —
/// i.e. identifiers the template reads from its render context that aren't part of llama.cpp's
/// fixed minja context (messages, tools, bos_token, etc.) and aren't locally bound by a
/// for-loop/set/macro/with. These are the custom knobs a model's template expects via
/// --chat-template-kwargs (e.g. Qwen3's enable_thinking, gpt-oss's reasoning_effort).
///
/// This is a best-effort heuristic lexer, not a spec-compliant Jinja2/minja parser — see
/// ChatTemplateVariableExtractor's class doc for documented punts (raw blocks, complex
/// subscripts, etc.).
/// </summary>
public interface IChatTemplateVariableExtractor
{
    /// <summary>
    /// Parses <paramref name="chatTemplate"/> and returns the free/custom variables found.
    /// Never throws — malformed/truncated template text degrades to a best-effort partial
    /// result (or an empty list) rather than an exception. Returns an empty list for
    /// null/empty input.
    /// </summary>
    IReadOnlyList<ChatTemplateVariable> Extract(string? chatTemplate);
}
