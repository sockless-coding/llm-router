using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Reads a Hugging Face checkpoint folder (<see cref="ModelFormat.Checkpoint"/>) into a library entry:
/// <c>config.json</c> for the architecture and shape (the text model's, for a multimodal one's
/// <c>text_config</c>), the quantization from the config or <c>quantize_config.json</c>, and the chat
/// template from <c>chat_template.jinja</c> or <c>tokenizer_config.json</c>.
/// </summary>
public static partial class CheckpointMetadataReader
{
    /// <summary>Whether <paramref name="folder"/> holds a checkpoint: a <c>config.json</c> and safetensors weights.</summary>
    public static bool IsCheckpointFolder(string folder) =>
        Directory.Exists(folder)
        && File.Exists(Path.Combine(folder, "config.json"))
        && Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.TopDirectoryOnly).Any();

    /// <summary>The bytes of every file in the folder (its subfolders included).</summary>
    public static long FolderSize(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    /// <summary>Fills <paramref name="model"/> from the checkpoint at its <see cref="LocalModel.FilePath"/>. False when there's no readable config.</summary>
    public static bool Apply(LocalModel model)
    {
        var folder = model.FilePath;
        JsonObject? config;
        try
        {
            config = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "config.json"))) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
        if (config is null) return false;

        JsonObject? quantizeConfig = null;
        try
        {
            var path = Path.Combine(folder, "quantize_config.json");
            if (File.Exists(path)) quantizeConfig = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        ApplyConfig(model, config, quantizeConfig, Path.GetFileName(folder.TrimEnd('/', '\\')));
        model.ChatTemplate = ReadChatTemplate(folder);
        return true;
    }

    /// <summary>The config's facts onto the entry — split from <see cref="Apply"/> so it can be checked without files.</summary>
    public static void ApplyConfig(LocalModel model, JsonObject config, JsonObject? quantizeConfig, string folderName)
    {
        // A multimodal model keeps its language model's shape under text_config (Qwen3.5, Gemma 4…).
        var text = config["text_config"] as JsonObject ?? config;

        model.Architecture = Str(config, "model_type") ?? Str(text, "model_type");
        // Not the architecture class: the library groups models by this name, and unrelated checkpoints share classes.
        model.GgufModelName = null;
        model.ContextLength = Int(text, "max_position_embeddings") ?? Int(config, "max_position_embeddings");
        model.BlockCount = Int(text, "num_hidden_layers");
        model.HeadCount = Int(text, "num_attention_heads");
        model.KvHeadCount = Int(text, "num_key_value_heads");
        model.EmbeddingLength = Int(text, "hidden_size");
        model.FeedForwardLength = Int(text, "intermediate_size");
        model.RopeFreqBase = Dbl(text, "rope_theta") ?? Dbl(text["rope_parameters"] as JsonObject, "rope_theta");
        model.QuantizationLevel = Quantization(config, quantizeConfig);
        model.ParameterSize = ParameterSizeFrom(folderName);
        model.DecisionType = null;
        model.PoolingType = null;
        model.DetectedMmprojPath = null;
        model.AllKvPairsJson = config.ToJsonString();
    }

    /// <summary>The model class transformers loads it with (<c>Qwen3_5ForConditionalGeneration</c>), from <c>architectures</c>.</summary>
    public static string? ArchitectureClass(JsonObject config) =>
        config["architectures"] is JsonArray a && a.FirstOrDefault() is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// A short label for the checkpoint's quantization: <c>MLX 4-bit g64</c> (mlx-lm writes
    /// <c>quantization</c>), <c>GPTQ 4-bit g128</c> / <c>AWQ</c> / <c>FP8</c> (<c>quantization_config</c>),
    /// NVIDIA ModelOpt's <c>NVFP4</c>, else the weights' dtype (<c>BF16</c>).
    /// </summary>
    public static string? Quantization(JsonObject config, JsonObject? quantizeConfig)
    {
        if (config["quantization"] is JsonObject mlx && Int(mlx, "bits") is { } mlxBits)
            return Label("MLX", mlxBits, Int(mlx, "group_size"));

        var qc = config["quantization_config"] as JsonObject ?? quantizeConfig;
        if (qc is not null)
        {
            var method = (Str(qc, "quant_method") ?? Str(qc, "method") ?? Str(qc, "quant_algo"))?.ToLowerInvariant();
            var algo = Str(qc, "quant_algo") ?? Str(qc["quantization"] as JsonObject, "quant_algo");
            if (algo is not null && algo.Contains("NVFP4", StringComparison.OrdinalIgnoreCase)) return "NVFP4";
            if (algo is not null && algo.Contains("FP8", StringComparison.OrdinalIgnoreCase)) return "FP8";
            var bits = Int(qc, "bits") ?? Int(qc, "w_bit");
            var group = Int(qc, "group_size") ?? Int(qc, "q_group_size");
            return method switch
            {
                "gptq" => Label("GPTQ", bits, group),
                "awq" => Label("AWQ", bits, group),
                "fp8" => "FP8",
                "exl3" or "exl2" => Label(method.ToUpperInvariant(), bits, null),
                "mxfp4" => "MXFP4",
                "bitsandbytes" => bits is { } b ? $"bnb {b}-bit" : "bitsandbytes",
                null => bits is { } b ? $"{b}-bit" : null,
                _ => bits is { } b ? $"{method.ToUpperInvariant()} {b}-bit" : method.ToUpperInvariant(),
            };
        }

        var dtype = Str(config, "torch_dtype") ?? Str(config, "dtype") ?? Str(config["text_config"] as JsonObject, "torch_dtype");
        return dtype switch
        {
            "bfloat16" => "BF16",
            "float16" => "F16",
            "float32" => "F32",
            null => null,
            _ => dtype.ToUpperInvariant(),
        };
    }

    /// <summary>Whether the label names MLX's own quantization, which only MLX-based engines (TensorFold) load.</summary>
    public static bool IsMlxQuantized(string? quantization) =>
        quantization?.StartsWith("MLX", StringComparison.Ordinal) == true;

    private static string Label(string method, int? bits, int? group) =>
        method + (bits is { } b ? $" {b}-bit" : "") + (group is { } g and > 0 ? $" g{g}" : "");

    /// <summary>The size in a checkpoint's name: <c>27B</c>, <c>30B-A3B</c>, <c>0.6B</c>.</summary>
    public static string? ParameterSizeFrom(string name)
    {
        var m = ParameterSizePattern().Match(name);
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    private static string? ReadChatTemplate(string folder)
    {
        try
        {
            var jinja = Path.Combine(folder, "chat_template.jinja");
            if (File.Exists(jinja)) return File.ReadAllText(jinja);
            var tokenizerConfig = Path.Combine(folder, "tokenizer_config.json");
            if (File.Exists(tokenizerConfig) && JsonNode.Parse(File.ReadAllText(tokenizerConfig)) is JsonObject tc)
                return tc["chat_template"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    private static double? Dbl(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    [GeneratedRegex(@"(?<![\w.])\d+(\.\d+)?[BM](-A\d+(\.\d+)?[BM])?(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ParameterSizePattern();
}
