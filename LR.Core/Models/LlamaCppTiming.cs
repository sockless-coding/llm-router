namespace LR.Core.Models;

/// <summary>
/// The phase of a llama.cpp timing event from stdout parsing.
/// </summary>
public enum LlamaCppTimingPhase
{
    /// <summary>Progress update during prompt processing.</summary>
    PromptProcessing,
    /// <summary>Progress update during token generation.</summary>
    Generation,
    /// <summary>Final completion summary with all metrics.</summary>
    Completion
}

/// <summary>
/// Accumulated timing data for a single llama.cpp task (identified by task_id).
/// Populated incrementally from stdout print_timing lines.
/// </summary>
public class LlamaCppTaskTiming
{
    /// <summary>The task ID from llama.cpp output.</summary>
    public int TaskId { get; set; }

    // --- Prompt metrics (from completion summary) ---
    /// <summary>Prompt evaluation time in milliseconds.</summary>
    public double? PromptEvalMs { get; set; }
    /// <summary>Number of prompt tokens processed.</summary>
    public int PromptTokens { get; set; }
    /// <summary>Prompt processing throughput (tokens/sec).</summary>
    public double? PromptTokensPerSec { get; set; }

    // --- Generation metrics (from completion summary) ---
    /// <summary>Token generation time in milliseconds.</summary>
    public double? EvalMs { get; set; }
    /// <summary>Number of generated tokens.</summary>
    public int GeneratedTokens { get; set; }
    /// <summary>Generation throughput (tokens/sec).</summary>
    public double? GenTokensPerSec { get; set; }

    // --- Total metrics ---
    /// <summary>Total time for the request in milliseconds.</summary>
    public double? TotalMs { get; set; }

    // --- Speculative decoding metrics (only if speculative decoding is active) ---
    /// <summary>Draft acceptance rate (0.0 to 1.0).</summary>
    public double? DraftAcceptanceRate { get; set; }
    /// <summary>Number of draft tokens accepted.</summary>
    public int DraftAccepted { get; set; }
    /// <summary>Number of draft tokens generated.</summary>
    public int DraftGenerated { get; set; }
    /// <summary>Mean length of accepted draft sequences.</summary>
    public double? DraftMeanLen { get; set; }

    // --- Progress tracking (from intermediate lines) ---
    /// <summary>Last seen prompt processing progress (0.0 to 1.0).</summary>
    public double PromptProgress { get; set; }
    /// <summary>Last seen generation tokens decoded count.</summary>
    public int NDecoded { get; set; }
}

/// <summary>
/// A parsed timing event from a single llama.cpp stdout line.
/// </summary>
public class LlamaCppTimingEvent
{
    public int TaskId { get; set; }
    public LlamaCppTimingPhase Phase { get; set; }

    // Prompt processing progress fields
    public double? Progress { get; set; }
    public int NTokens { get; set; }
    public double? TokensPerSec { get; set; }

    // Generation progress fields
    public int NDecoded { get; set; }
    public double? GenTokensPerSec { get; set; }
    public double? Gen3sTokensPerSec { get; set; }

    // Completion summary fields
    public double? PromptEvalMs { get; set; }
    public int PromptTokens { get; set; }
    public double? PromptMsPerToken { get; set; }
    public double? PromptTokensPerSec { get; set; }

    public double? EvalMs { get; set; }
    public int GeneratedTokens { get; set; }
    public double? GenMsPerToken { get; set; }
    public double? GenTokensPerSecCompletion { get; set; }

    public double? TotalMs { get; set; }
    public int TotalTokens { get; set; }

    // Speculative decoding (completion only)
    public double? DraftAcceptanceRate { get; set; }
    public int DraftAccepted { get; set; }
    public int DraftGenerated { get; set; }
    public double? DraftMeanLen { get; set; }
}