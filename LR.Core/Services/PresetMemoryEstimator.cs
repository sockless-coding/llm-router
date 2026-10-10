using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Rough device-memory footprint of a loaded preset, for memory-group budgeting — deliberately
/// simple, and overridable per preset via <see cref="ModelPreset.MemoryEstimateMb"/> when it's off:
/// <list type="bullet">
/// <item>Weights: on-disk size of the model (all split shards), draft model and projector,
/// scaled down for partial offload (<c>-ngl</c> below the layer count) and MoE expert offload
/// (<c>--cpu-moe</c> / <c>--n-cpu-moe</c>, assuming experts are ~3/4 of the weights).</item>
/// <item>KV cache: context × layers × KV heads × head dim × (K + V element size), from the
/// linked library model's metadata; omitted when that metadata is unknown or <c>--no-kv-offload</c>.</item>
/// <item>Overhead: 5% of weights plus 512 MB of compute buffers.</item>
/// </list>
/// </summary>
public static class PresetMemoryEstimator
{
    private const long Mb = 1024 * 1024;
    private const long ComputeBufferBytes = 512 * Mb;
    private const double ExpertShare = 0.75;

    public static long EstimateBytes(ModelPreset preset, LocalModel? model, Func<string, long?> fileSize)
    {
        if (preset.MemoryEstimateMb is > 0)
            return preset.MemoryEstimateMb.Value * Mb;

        long weights = ModelFileBytes(preset.ModelPath, fileSize);
        int? layers = model?.BlockCount;

        if (preset.GpuLayers is int ngl and >= 0 && layers is > 0 && ngl < layers)
            weights = (long)(weights * ((double)ngl / layers.Value));

        if (preset.CpuMoe == true)
            weights = (long)(weights * (1 - ExpertShare));
        else if (preset.NCpuMoe is > 0 && layers is > 0)
            weights = (long)(weights * (1 - ExpertShare * Math.Min(1.0, (double)preset.NCpuMoe.Value / layers.Value)));

        if (!string.IsNullOrWhiteSpace(preset.SpecDraftModel))
            weights += ModelFileBytes(preset.SpecDraftModel, fileSize);
        if (!string.IsNullOrWhiteSpace(preset.Mmproj))
            weights += fileSize(preset.Mmproj) ?? 0;

        return weights + KvCacheBytes(preset, model) + (long)(weights * 0.05) + ComputeBufferBytes;
    }

    /// <summary>Bytes per KV-cache element for a <c>-ctk</c>/<c>-ctv</c> type (block-quant sizes included).</summary>
    public static double KvElementBytes(string? cacheType) => cacheType?.ToLowerInvariant() switch
    {
        "f32" => 4,
        "q8_0" => 34.0 / 32,
        "q5_1" => 24.0 / 32,
        "q5_0" => 22.0 / 32,
        "q4_1" => 20.0 / 32,
        "q4_0" or "iq4_nl" => 18.0 / 32,
        _ => 2, // f16 / bf16 / llama.cpp's default
    };

    private static long KvCacheBytes(ModelPreset preset, LocalModel? model)
    {
        if (preset.KvOffload == false)
            return 0;
        if (model is not { BlockCount: > 0, HeadCount: > 0, EmbeddingLength: > 0 })
            return 0;

        long context = preset.ContextSize is > 0 ? preset.ContextSize.Value : preset.GgufContextLength ?? model.ContextLength ?? 4096;
        int kvHeads = model.KvHeadCount is > 0 ? model.KvHeadCount.Value : model.HeadCount!.Value;
        double headDim = (double)model.EmbeddingLength!.Value / model.HeadCount!.Value;
        double perToken = model.BlockCount!.Value * kvHeads * headDim
            * (KvElementBytes(preset.CacheTypeK) + KvElementBytes(preset.CacheTypeV));

        return (long)(context * perToken);
    }

    private static long ModelFileBytes(string? path, Func<string, long?> fileSize)
    {
        if (string.IsNullOrWhiteSpace(path))
            return 0;

        return SplitGguf.AllShards(path).Sum(shard => fileSize(shard) ?? 0);
    }

    /// <summary>Default file-size lookup: the file's length on disk, or null if it can't be read.</summary>
    public static long? FileSizeOnDisk(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch { return null; }
    }
}
