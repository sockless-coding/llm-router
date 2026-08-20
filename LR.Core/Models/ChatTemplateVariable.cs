namespace LR.Core.Models;

/// <summary>
/// A free (non-standard) variable referenced by a Jinja2 chat template, discovered by
/// <see cref="Interfaces.IChatTemplateVariableExtractor"/>. Represents a custom knob the
/// template expects via --chat-template-kwargs (e.g. enable_thinking, reasoning_effort).
/// </summary>
public record ChatTemplateVariable
{
    /// <summary>
    /// The free variable's identifier name, e.g. "reasoning_effort".
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Best-effort string literals the variable is compared/defaulted against syntactically
    /// (==, !=, "in [...]" membership, default(...) filter arg). Heuristic — may be incomplete
    /// or include false positives from unrelated code paths. Empty if none found.
    /// </summary>
    public IReadOnlyList<string> LiteralValues { get; init; } = Array.Empty<string>();
}
