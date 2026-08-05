using System.Text.Json.Serialization;

namespace LR.Core.Models;

/// <summary>
/// Metadata extracted from a GGUF file header.
/// </summary>
public class GgufMetadata
{
    /// <summary>
    /// Architecture name (e.g. "llama", "gemma2", "phi3").
    /// From general.architecture.
    /// </summary>
    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }

    /// <summary>
    /// Model name from the file (e.g. "Llama-2-7B-chat").
    /// From general.name.
    /// </summary>
    [JsonPropertyName("modelName")]
    public string? ModelName { get; set; }

    /// <summary>
    /// Human-readable parameter size (e.g. "7B", "13B").
    /// Computed from general.parameter_count if available, otherwise null.
    /// </summary>
    [JsonPropertyName("parameterSize")]
    public string? ParameterSize { get; set; }

    /// <summary>
    /// Quantization level (e.g. "Q4_K_M", "Q8_0").
    /// Mapped from general.file_type.
    /// </summary>
    [JsonPropertyName("quantizationLevel")]
    public string? QuantizationLevel { get; set; }

    /// <summary>
    /// Context length (e.g. 4096, 8192).
    /// From {arch}.context_length.
    /// </summary>
    [JsonPropertyName("contextLength")]
    public int? ContextLength { get; set; }

    /// <summary>
    /// Embedding length (e.g. 4096, 3072).
    /// From {arch}.embedding_length.
    /// </summary>
    [JsonPropertyName("embeddingLength")]
    public int? EmbeddingLength { get; set; }

    /// <summary>
    /// Feed-forward length (e.g. 14336, 2048).
    /// From {arch}.feed_forward_length.
    /// </summary>
    [JsonPropertyName("feedForwardLength")]
    public int? FeedForwardLength { get; set; }

    /// <summary>
    /// Number of transformer blocks (e.g. 32, 40).
    /// From {arch}.block_count.
    /// </summary>
    [JsonPropertyName("blockCount")]
    public int? BlockCount { get; set; }

    /// <summary>
    /// Attention head count (e.g. 32, 40).
    /// From {arch}.attention.head_count.
    /// </summary>
    [JsonPropertyName("headCount")]
    public int? HeadCount { get; set; }

    /// <summary>
    /// KV head count (e.g. 8, 40 for GQA).
    /// From {arch}.attention.head_count_kv.
    /// </summary>
    [JsonPropertyName("kvHeadCount")]
    public int? KvHeadCount { get; set; }

    /// <summary>
    /// RoPE base frequency (e.g. 10000).
    /// From {arch}.rope.freq_base.
    /// </summary>
    [JsonPropertyName("ropeFreqBase")]
    public double? RopeFreqBase { get; set; }

    /// <summary>
    /// EOS token ID (e.g. 128001).
    /// From tokenizer.ggml.eos_token_id.
    /// </summary>
    [JsonPropertyName("eosTokenId")]
    public int? EosTokenId { get; set; }

    /// <summary>
    /// BOS token ID (e.g. 128000).
    /// From tokenizer.ggml.bos_token_id.
    /// </summary>
    [JsonPropertyName("bosTokenId")]
    public int? BosTokenId { get; set; }

    /// <summary>
    /// Chat template string (e.g. "{% for message in messages %}...").
    /// From tokenizer.chat_template.
    /// </summary>
    [JsonPropertyName("chatTemplate")]
    public string? ChatTemplate { get; set; }

    /// <summary>
    /// License text from the GGUF file.
    /// From general.license (may be large).
    /// </summary>
    [JsonPropertyName("licenseText")]
    public string? LicenseText { get; set; }

    /// <summary>
    /// Raw key-value pairs from the GGUF header for model_info in /api/show.
    /// Excludes very large binary arrays (tokenizer tokens/merges/scores).
    /// </summary>
    [JsonPropertyName("allKvPairs")]
    public Dictionary<string, object>? AllKvPairs { get; set; }
}
