using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Which server engines can run a library model — shown on the Models page, and used to offer only
/// fitting models in a preset's library picker. Decided by what the file is, not by guessing at model
/// families, except where an engine itself keys off the name (Strata's Qwen3.8 Flash Next GGUFs).
/// <list type="bullet">
/// <item>GGUF: llama.cpp for chat, embedding, reranking and decision models and projectors;
/// stable-diffusion.cpp for diffusion models and their parts; Strata also for Flash Next.</item>
/// <item>Safetensors file: stable-diffusion.cpp (diffusion checkpoints and components).</item>
/// <item>Checkpoint folder: TensorFold, and vLLM unless it's MLX-quantized (a format only MLX
/// runtimes read). TensorFold refuses at start a family it doesn't serve.</item>
/// </list>
/// </summary>
public static class ModelEngineSupport
{
    public static IReadOnlyList<ServerEngine> For(LocalModel model)
    {
        switch (model.Format)
        {
            case ModelFormat.Checkpoint:
                return CheckpointMetadataReader.IsMlxQuantized(model.QuantizationLevel)
                    ? [ServerEngine.TensorFold]
                    : [ServerEngine.TensorFold, ServerEngine.Vllm];

            case ModelFormat.Safetensors:
                return [ServerEngine.StableDiffusionCpp];
        }

        var type = ModelKindClassifier.Classify(model);
        if (type is ModelType.Image or ModelType.ImageComponent)
            return [ServerEngine.StableDiffusionCpp];
        if (type == ModelType.Chat && IsStrataModel(model))
            return [ServerEngine.LlamaCpp, ServerEngine.Strata];
        return [ServerEngine.LlamaCpp];
    }

    public static bool Supports(LocalModel model, ServerEngine engine) => For(model).Contains(engine);

    /// <summary>Strata prepares Qwen3.8-Flash-Next GGUFs, recognised (as its setup recognises them) by name.</summary>
    private static bool IsStrataModel(LocalModel model)
    {
        var name = Path.GetFileName(model.FilePath) + " " + model.GgufModelName;
        return name.Contains("Flash-Next", StringComparison.OrdinalIgnoreCase)
            || name.Contains("FlashNext", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Flash Next", StringComparison.OrdinalIgnoreCase);
    }
}
