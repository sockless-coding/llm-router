using System.Buffers.Binary;
using System.Text;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Minimal binary reader for GGUF file headers.
/// Reads magic, version, tensor count, KV pair count, and all KV pairs.
/// Does NOT read tensor data — only metadata from the header.
/// </summary>
public class GgufMetadataReader : IGgufMetadataReader
{
    /// <summary>
    /// Maps GGUF file_type integer to human-readable quantization level string.
    /// Based on llama.cpp ggml_ftype enum (https://github.com/ggerganov/llama.cpp/blob/master/include/ggml.h)
    /// </summary>
    private static readonly Dictionary<int, string> QuantizationMap = new()
    {
        { 0,  "F32" },
        { 1,  "F16" },
        { 2,  "Q4_0" },
        { 3,  "Q4_1" },
        { 6,  "Q5_0" },
        { 7,  "Q5_1" },
        { 8,  "Q8_0" },
        { 9,  "Q2_K" },
        { 10, "Q3_K_S" },
        { 11, "Q3_K_M" },
        { 12, "Q3_K_L" },
        { 13, "Q4_K_S" },
        { 14, "Q4_K_M" },
        { 15, "Q5_K_S" },
        { 16, "Q5_K_M" },
        { 17, "Q6_K" },
        { 18, "Q8_K_S" },
        { 19, "Q8_K_M" },
        { 20, "IQ4_NL" },
        { 21, "IQ3_XS" },
        { 22, "IQ3_XXS" },
        { 23, "IQ2_XXS" },
        { 24, "IQ2_XS" },
        { 25, "IQ4_XS" },
        { 26, "IQ1_S" },
        { 27, "IQ1_M" },
        { 28, "BSQ3" }
    };

    /// <summary>
    /// Keys to exclude from model_info (they contain very large binary/token data).
    /// </summary>
    private static readonly HashSet<string> ExcludedKeys = new()
    {
        "tokenizer.ggml.tokens",
        "tokenizer.ggml.scores",
        "tokenizer.ggml.merges",
        "tokenizer.hf.vocabulary",
        "tokenizer.ggml.token_type"
    };

    public async Task<GgufMetadata?> ReadAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using var fs = File.OpenRead(filePath);
            using var reader = new BinaryReader(fs, Encoding.UTF8, true);

            // Read magic: "GGUF" (4 bytes)
            var magicBytes = reader.ReadBytes(4);
            if (magicBytes.Length < 4 || !Encoding.ASCII.GetString(magicBytes).Equals("GGUF", StringComparison.Ordinal))
                return null; // Not a GGUF file

            // Read version (uint32, little-endian)
            var version = reader.ReadUInt32();
            if (version < 1 || version > 4) // Only support known versions
                return null;

            // Read tensor count and KV pair count (both uint64)
            var _tensorCount = reader.ReadUInt64(); // We don't need tensors for metadata
            var kvPairCount = reader.ReadUInt64();

            // Parse all KV pairs
            var rawKvPairs = new Dictionary<string, object>();
            for (ulong i = 0; i < kvPairCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var key = ReadString(reader);
                var valueType = (GgufValueType)reader.ReadUInt32();
                var value = ReadValue(reader, valueType);
                rawKvPairs[key] = value;
            }

            // Build GgufMetadata from parsed KV pairs
            return BuildMetadata(rawKvPairs);
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate cancellation
        }
        catch
        {
            // File may be corrupted or inaccessible
            return null;
        }
    }

    private GgufMetadata BuildMetadata(Dictionary<string, object> rawKvPairs)
    {
        var metadata = new GgufMetadata();

        // Get architecture name
        metadata.Architecture = GetString(rawKvPairs, "general.architecture") ?? "llama";
        metadata.ModelName = GetString(rawKvPairs, "general.name");

        // Parameter count → human-readable size
        if (TryGetLong(rawKvPairs, "general.parameter_count", out var paramCount))
            metadata.ParameterSize = FormatParameterSize(paramCount);

        // File type → quantization level
        if (TryGetInt(rawKvPairs, "general.file_type", out var fileType))
            metadata.QuantizationLevel = QuantizationMap.GetValueOrDefault(fileType, $"Unknown({fileType})");

        // Architecture-specific fields using the arch name as prefix
        var arch = metadata.Architecture;
        if (TryGetInt(rawKvPairs, $"{arch}.context_length", out var ctxLen))
            metadata.ContextLength = ctxLen;
        if (TryGetInt(rawKvPairs, $"{arch}.embedding_length", out var embLen))
            metadata.EmbeddingLength = embLen;
        if (TryGetInt(rawKvPairs, $"{arch}.feed_forward_length", out var ffLen))
            metadata.FeedForwardLength = ffLen;
        if (TryGetInt(rawKvPairs, $"{arch}.block_count", out var blocks))
            metadata.BlockCount = blocks;
        if (TryGetInt(rawKvPairs, $"{arch}.attention.head_count", out var heads))
            metadata.HeadCount = heads;
        if (TryGetInt(rawKvPairs, $"{arch}.attention.head_count_kv", out var kvHeads))
            metadata.KvHeadCount = kvHeads;

        // Rope freq base can be float or int depending on the GGUF version
        if (TryGetDouble(rawKvPairs, $"{arch}.rope.freq_base", out var ropeBase))
            metadata.RopeFreqBase = ropeBase;

        // Tokenizer fields
        if (TryGetInt(rawKvPairs, "tokenizer.ggml.eos_token_id", out var eosId))
            metadata.EosTokenId = eosId;
        if (TryGetInt(rawKvPairs, "tokenizer.ggml.bos_token_id", out var bosId))
            metadata.BosTokenId = bosId;

        // Chat template
        metadata.ChatTemplate = GetString(rawKvPairs, "tokenizer.chat_template")
                           ?? GetString(rawKvPairs, "tokenizer.ggml.template");

        // License text (can be large)
        metadata.LicenseText = GetString(rawKvPairs, "general.license");

        // Build model_info dictionary — exclude very large arrays
        var modelInfo = new Dictionary<string, object>();
        foreach (var kvp in rawKvPairs)
        {
            if (ExcludedKeys.Contains(kvp.Key))
                continue;

            var jsonSafeValue = ConvertToJsonSafe(kvp.Value);
            if (jsonSafeValue is not null)
                modelInfo[kvp.Key] = jsonSafeValue;
        }
        metadata.AllKvPairs = modelInfo.Count > 0 ? modelInfo : null;

        return metadata;
    }

    /// <summary>
    /// Converts a parameter count (e.g. 7_000_000_000) to a human-readable string ("7B").
    /// </summary>
    private static string FormatParameterSize(long count)
    {
        if (count >= 1_000_000_000_000L)
            return $"{count / 1_000_000_000_000L}T";
        if (count >= 1_000_000_000L)
        {
            var billions = count / 1_000_000_000.0;
            return billions == Math.Floor(billions)
                ? $"{billions:B0}B"
                : $"{billions:F1}B";
        }
        if (count >= 1_000_000L)
            return $"{count / 1_000_000.0:F0}M";
        return count.ToString();
    }

    /// <summary>
    /// Converts a raw GGUF value to a JSON-safe type.
    /// </summary>
    private static object? ConvertToJsonSafe(object value)
    {
        return value switch
        {
            bool b => b,
            int i => i,
            long l => l,
            double d when double.IsNaN(d) || double.IsInfinity(d) => null,
            double d => d,
            string s => s,
            byte[] bytes => Convert.ToBase64String(bytes),
            List<object> list => new List<object>(list.Select(ConvertToJsonSafe).OfType<object>().ToList()),
            _ => value.ToString()
        };
    }

    #region GGUF Binary Reading Helpers

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length == 0) return string.Empty;
        var bytes = reader.ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadValue(BinaryReader reader, GgufValueType valueType)
    {
        return valueType switch
        {
            GgufValueType.UINT8 => reader.ReadByte(),
            GgufValueType.INT8 => reader.ReadSByte(),
            GgufValueType.UINT16 => reader.ReadUInt16(),
            GgufValueType.INT16 => reader.ReadInt16(),
            GgufValueType.UINT32 => reader.ReadUInt32(),
            GgufValueType.INT32 => reader.ReadInt32(),
            GgufValueType.FLOAT32 => reader.ReadSingle(),
            GgufValueType.UINT64 => reader.ReadUInt64(),
            GgufValueType.INT64 => reader.ReadInt64(),
            GgufValueType.FLOAT64 => reader.ReadDouble(),
            GgufValueType.BOOL => reader.ReadBoolean(),
            GgufValueType.STRING => ReadString(reader),
            GgufValueType.ARRAY => ReadArray(reader),
            _ => null
        };
    }

    private static List<object> ReadArray(BinaryReader reader)
    {
        var arrayType = (GgufValueType)reader.ReadUInt32();
        var count = reader.ReadUInt64();
        var list = new List<object>();

        for (ulong i = 0; i < count; i++)
        {
            // For large arrays (tokenizer tokens can be 100K+ items), read but don't store individual values
            if (count > 50_000)
            {
                SkipValue(reader, arrayType);
            }
            else
            {
                var value = ReadValue(reader, arrayType);
                list.Add(value ?? new object());
            }
        }

        return list;
    }

    private static void SkipValue(BinaryReader reader, GgufValueType valueType)
    {
        switch (valueType)
        {
            case GgufValueType.UINT8:
            case GgufValueType.INT8:
                reader.BaseStream.Seek(1, SeekOrigin.Current);
                break;
            case GgufValueType.UINT16:
            case GgufValueType.INT16:
                reader.BaseStream.Seek(2, SeekOrigin.Current);
                break;
            case GgufValueType.UINT32:
            case GgufValueType.INT32:
            case GgufValueType.FLOAT32:
                reader.BaseStream.Seek(4, SeekOrigin.Current);
                break;
            case GgufValueType.UINT64:
            case GgufValueType.INT64:
            case GgufValueType.FLOAT64:
                reader.BaseStream.Seek(8, SeekOrigin.Current);
                break;
            case GgufValueType.BOOL:
                reader.BaseStream.Seek(1, SeekOrigin.Current);
                break;
            case GgufValueType.STRING:
                var strLen = reader.ReadUInt64();
                reader.BaseStream.Seek((long)strLen, SeekOrigin.Current);
                break;
            case GgufValueType.ARRAY:
                var arrType = (GgufValueType)reader.ReadUInt32();
                var arrCount = reader.ReadUInt64();
                for (ulong i = 0; i < arrCount; i++)
                    SkipValue(reader, arrType);
                break;
            default:
                // Unknown type — can't skip
                throw new InvalidDataException($"Unknown GGUF value type: {valueType}");
        }
    }

    #endregion

    #region Dictionary Helpers

    private static string? GetString(Dictionary<string, object> dict, string key)
    {
        return dict.TryGetValue(key, out var val) && val is string s ? s : null;
    }

    private static bool TryGetInt(Dictionary<string, object> dict, string key, out int value)
    {
        if (dict.TryGetValue(key, out var val))
        {
            if (val is int i)
            {
                value = i;
                return true;
            }
            if (val is long l && l >= int.MinValue && l <= int.MaxValue)
            {
                value = (int)l;
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static bool TryGetLong(Dictionary<string, object> dict, string key, out long value)
    {
        if (dict.TryGetValue(key, out var val))
        {
            if (val is long l)
            {
                value = l;
                return true;
            }
            if (val is int i)
            {
                value = i;
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static bool TryGetDouble(Dictionary<string, object> dict, string key, out double value)
    {
        if (dict.TryGetValue(key, out var val))
        {
            if (val is double d)
            {
                value = d;
                return true;
            }
            if (val is float f)
            {
                value = f;
                return true;
            }
            if (val is int i)
            {
                value = i;
                return true;
            }
        }
        value = 0.0;
        return false;
    }

    #endregion
}

/// <summary>
/// GGUF value types as defined in the GGUF specification.
/// https://github.com/ggerganov/llama.cpp/blob/master/gguf-py/src/gguf_ggml_header.py
/// </summary>
public enum GgufValueType : uint
{
    UINT8 = 0,
    INT8 = 1,
    UINT16 = 2,
    INT16 = 3,
    UINT32 = 4,
    INT32 = 5,
    FLOAT32 = 6,
    BOOL = 7,
    STRING = 8,
    ARRAY = 9,
    FLOAT64 = 10,
    UINT64 = 11,
    INT64 = 12
}
