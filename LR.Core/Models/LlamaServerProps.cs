namespace LR.Core.Models;

/// <summary>
/// A snapshot of the interesting fields from a running llama.cpp server's <c>/props</c> response.
/// Read once per server lifetime after it first reports healthy (see the llama.cpp provider) and
/// used for capacity limiting, the Server Detail runtime panel, and live model-capability
/// reporting. Every field is nullable — <c>/props</c> shape varies by llama.cpp version.
/// </summary>
public sealed record LlamaServerProps
{
    /// <summary>
    /// <c>total_slots</c> — the resolved <c>--parallel</c>/<c>-np</c>: how many requests the
    /// server processes concurrently.
    /// </summary>
    public int? TotalSlots { get; init; }

    /// <summary>
    /// <c>default_generation_settings.n_ctx</c> — the context window a single request/slot gets.
    /// With more than one slot this is the total context divided across slots, so it is the
    /// figure a client should budget one request against.
    /// </summary>
    public int? ContextSizePerSlot { get; init; }

    /// <summary><c>modalities.vision</c> — the loaded model accepts image input.</summary>
    public bool? Vision { get; init; }

    /// <summary><c>modalities.audio</c> — the loaded model accepts audio input.</summary>
    public bool? Audio { get; init; }

    /// <summary><c>model_path</c> — absolute path of the GGUF the server actually loaded.</summary>
    public string? ModelPath { get; init; }

    /// <summary><c>build_info</c> — the llama.cpp build string (commit + compiler).</summary>
    public string? BuildInfo { get; init; }
}
