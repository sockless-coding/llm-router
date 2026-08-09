using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Persisted state for one hop of an OpenAI Responses API conversation. Stores only the input
/// items and output items produced by THIS turn — not a denormalized copy of the whole
/// conversation — so a `previous_response_id` chain is reconstructed by walking
/// <see cref="PreviousResponseId"/> pointers backward (see LR.Core/Services/ResponseChainBuilder.cs).
/// Rows are only inserted when the request's `store` field is true.
/// </summary>
[Table("StoredResponses")]
public class StoredResponse
{
    [Key, MaxLength(64)]
    public string Id { get; set; } = $"resp_{Guid.NewGuid():N}";

    [Required]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Not an EF foreign key — the ancestor row may have been created with store:false, since
    /// been deleted, or (in principle) not exist, and the chain-walk tolerates all of those.
    /// </summary>
    [MaxLength(64)]
    public string? PreviousResponseId { get; set; }

    [MaxLength(128)]
    public string Model { get; set; } = string.Empty;

    [Column(TypeName = "TEXT")]
    public string? Instructions { get; set; }

    /// <summary>This turn's `input` items, serialized as JSON.</summary>
    [Column(TypeName = "TEXT")]
    public string OwnInputItemsJson { get; set; } = "[]";

    /// <summary>This turn's `output` items, serialized as JSON.</summary>
    [Column(TypeName = "TEXT")]
    public string OwnOutputItemsJson { get; set; } = "[]";

    /// <summary>"queued" | "in_progress" | "completed" | "failed" | "incomplete" | "cancelled".</summary>
    [MaxLength(32)]
    public string Status { get; set; } = "in_progress";

    [MaxLength(4096)]
    public string? ErrorMessage { get; set; }

    public bool Store { get; set; } = true;

    public bool Background { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    [Column(TypeName = "TEXT")]
    public string? ToolsJson { get; set; }

    [Column(TypeName = "TEXT")]
    public string? ToolChoiceJson { get; set; }

    [Column(TypeName = "TEXT")]
    public string? MetadataJson { get; set; }
}
