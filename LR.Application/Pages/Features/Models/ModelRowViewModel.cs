using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Models;

/// <summary>
/// What the <c>_ModelRow</c> partial needs to render one row of the Models index table, whether
/// that row stands alone or sits inside a collapsible model group.
/// </summary>
public class ModelRowViewModel
{
    public required LocalModel Model { get; set; }
    public int UsageCount { get; set; }

    /// <summary>True when this row is a member of a group and should be visually nested under it.</summary>
    public bool Indented { get; set; }

    /// <summary>True when this row starts collapsed, revealed only by toggling its group open.</summary>
    public bool HiddenByDefault { get; set; }

    /// <summary>The DOM id JS toggling matches against; null for a standalone (ungrouped) row.</summary>
    public string? GroupDomId { get; set; }

    /// <summary>
    /// The enclosing group's type. A nested row whose type matches it leaves its type badge out —
    /// the group header already shows it — so only the odd ones out (a projector, a VAE) stand out.
    /// </summary>
    public ModelType? GroupType { get; set; }
}

/// <summary>What the <c>_ModelTypeBadge</c> partial renders: a type's colour, with its label.</summary>
/// <param name="Detail">Secondary text after the label, e.g. the decision type ("openjev").</param>
public sealed record ModelTypeBadgeViewModel(ModelType Type, string Label, string? Detail = null);
