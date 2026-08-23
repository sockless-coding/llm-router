using LR.Core.Interfaces;

namespace LR.Core.Services;

/// <summary>
/// Shared reasoning/thinking capability detection used by both the Ollama and OpenAI-compatible
/// protocol handlers so a model's capabilities are reported consistently regardless of which API
/// a client queries.
/// </summary>
public static class ReasoningCapabilityDetector
{
    /// <summary>
    /// Free variable names that, when a chat template reads them, signal a reasoning/thinking
    /// toggle the template exposes via --chat-template-kwargs.
    /// </summary>
    private static readonly HashSet<string> ReasoningSignalVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "enable_thinking", "thinking", "reasoning_effort", "thinking_budget"
    };

    /// <summary>
    /// "thinking" capability: the preset has reasoning explicitly enabled, or the chat template
    /// itself emits a thinking block (e.g. "&lt;think&gt;") or references a known reasoning-toggle
    /// variable (e.g. "enable_thinking", "reasoning_effort") detected via
    /// <see cref="IChatTemplateVariableExtractor"/>.
    /// </summary>
    public static bool SupportsThinking(string? reasoning, string? chatTemplate, IChatTemplateVariableExtractor templateVariableExtractor)
    {
        if (!string.IsNullOrEmpty(reasoning) && !reasoning.Equals("off", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(chatTemplate) && chatTemplate.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            return true;

        return templateVariableExtractor.Extract(chatTemplate).Any(v => ReasoningSignalVariables.Contains(v.Name));
    }

    /// <summary>
    /// Narrower than <see cref="SupportsThinking"/>: specifically whether the chat template reads
    /// a "reasoning_effort" (or "thinking_budget") variable, i.e. a client can steer *how much*
    /// the model reasons per-request rather than merely toggling reasoning on/off.
    /// </summary>
    public static bool SupportsReasoningEffort(string? chatTemplate, IChatTemplateVariableExtractor templateVariableExtractor) =>
        templateVariableExtractor.Extract(chatTemplate).Any(v =>
            v.Name.Equals("reasoning_effort", StringComparison.OrdinalIgnoreCase) ||
            v.Name.Equals("thinking_budget", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The discrete effort levels a "reasoning_effort" chat-template variable is checked
    /// against (e.g. "low", "medium", "high"), as heuristically collected by
    /// <see cref="IChatTemplateVariableExtractor"/> from the template's own comparisons/defaults.
    /// Empty when the template doesn't reference "reasoning_effort" at all, or references it only
    /// in ways the heuristic can't pin down a fixed set of values for (e.g. a numeric budget).
    /// </summary>
    public static IReadOnlyList<string> GetReasoningEffortOptions(string? chatTemplate, IChatTemplateVariableExtractor templateVariableExtractor)
    {
        var variable = templateVariableExtractor.Extract(chatTemplate)
            .FirstOrDefault(v => v.Name.Equals("reasoning_effort", StringComparison.OrdinalIgnoreCase));
        return variable?.LiteralValues ?? Array.Empty<string>();
    }
}
