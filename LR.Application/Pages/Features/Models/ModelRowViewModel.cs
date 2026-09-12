using LR.Core.Models;

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
}
