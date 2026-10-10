using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// One recorded chat turn sent from the Playground page, with the performance figures measured
/// for it and a snapshot of the settings it ran under — kept so different preset settings can be
/// compared against each other later, even after the preset itself has been edited or deleted.
/// Unlike <see cref="ModelStatistics"/> and <see cref="ApiRequestLog"/>, these are deliberately
/// kept out of retention cleanup: they're a benchmark record, not a rolling log.
/// </summary>
[Table("PlaygroundRuns")]
public class PlaygroundRun
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    // --- What ran ---

    /// <summary>The preset the turn was sent to. Set null if the preset is later deleted.</summary>
    public Guid? PresetId { get; set; }

    /// <summary>Preset name at the time of the run (survives renames/deletion).</summary>
    [Required, MaxLength(256)]
    public string PresetName { get; set; } = string.Empty;

    /// <summary>Name of the server instance owning the preset at the time of the run.</summary>
    [MaxLength(256)]
    public string? ServerName { get; set; }

    public ServerEngine? Engine { get; set; }

    /// <summary>Model file name (no directory) the preset pointed at.</summary>
    [MaxLength(512)]
    public string? ModelFile { get; set; }

    /// <summary>
    /// The launch command line the preset generated at the time of the run — the full record of
    /// load-time settings (context, GPU layers, cache types, batch sizes, speculative decoding...).
    /// </summary>
    public string? SettingsSnapshot { get; set; }

    /// <summary>
    /// Short hash of <see cref="SettingsSnapshot"/>, so runs made under identical launch settings
    /// can be grouped together and runs made after a preset edit can be told apart.
    /// </summary>
    [MaxLength(16)]
    public string? SettingsHash { get; set; }

    /// <summary>
    /// Request-level sampling parameters sent with the turn (temperature, max_tokens, ...), as JSON.
    /// </summary>
    public string? RequestParams { get; set; }

    /// <summary>Free-text label the user attached to the run, e.g. "fa=on ub=1024".</summary>
    [MaxLength(256)]
    public string? Label { get; set; }

    /// <summary>Groups the turns of one Playground conversation together.</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// True when the preset's model wasn't loaded on its server when the turn was sent, so the
    /// latency figures include a model (re)load.
    /// </summary>
    public bool ColdStart { get; set; }

    // --- Conversation ---

    /// <summary>Number of messages (including the system prompt) sent with the turn.</summary>
    public int MessageCount { get; set; }

    /// <summary>The user message that started the turn.</summary>
    public string? Prompt { get; set; }

    public string? Response { get; set; }

    public string? Reasoning { get; set; }

    [MaxLength(32)]
    public string? FinishReason { get; set; }

    /// <summary>Error message when the turn failed or was stopped early.</summary>
    public string? Error { get; set; }

    // --- Client-measured latency (browser clock, includes router + network overhead) ---

    /// <summary>Time from sending the request to the first content or reasoning token.</summary>
    public double? TimeToFirstTokenMs { get; set; }

    /// <summary>Time from sending the request to the end of the stream.</summary>
    public double? TotalMs { get; set; }

    // --- Backend-reported figures (llama.cpp timings) ---

    public int PromptTokens { get; set; }

    /// <summary>Prompt tokens served from the KV cache rather than evaluated.</summary>
    public int? CachedTokens { get; set; }

    public int CompletionTokens { get; set; }

    public double? PromptMs { get; set; }
    public double? PromptTokensPerSec { get; set; }
    public double? GenerationMs { get; set; }
    public double? GenTokensPerSec { get; set; }

    /// <summary>Draft tokens accepted during speculative decoding, when it was active.</summary>
    public int? DraftAccepted { get; set; }

    /// <summary>Draft tokens proposed during speculative decoding, when it was active.</summary>
    public int? DraftGenerated { get; set; }

    public ModelPreset? Preset { get; set; }
}
