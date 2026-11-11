using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// What a registered model file is for — which API serves it, or, for a companion file, what it
/// gets paired with. Drives the Models page's type badges and filter tabs, and which library
/// files are offered where (e.g. only projectors in a preset's mmproj picker).
/// </summary>
/// <remarks>
/// A draft/MTP head for self-speculative decoding was previously guessed from a "mtp" substring
/// in the filename, but that's not reliable: some repos embed the MTP head directly in the main
/// quant file and still put "MTP" in that file's name (e.g. "Qwopus3.6-27B-v2-MTP-Q6_K.gguf" is
/// the full model, not a sidecar), so the filename alone can't tell the two apart. Unlike mmproj,
/// there's no GGUF metadata field that reliably distinguishes a real standalone MTP-head sidecar
/// file either, so that guess was dropped rather than replaced with another unverified heuristic.
/// </remarks>
public enum ModelType
{
    /// <summary>A text-generation model — chat/completions.</summary>
    Chat = 0,
    /// <summary>Pools a vector per input — <c>/v1/embeddings</c>.</summary>
    Embedding = 1,
    /// <summary>Scores query/document pairs — llama.cpp's rank pooling.</summary>
    Reranker = 2,
    /// <summary>A native decision model — <c>/v1/systemone</c>.</summary>
    Decision = 3,
    /// <summary>A diffusion checkpoint stable-diffusion.cpp runs — <c>/v1/images/*</c>.</summary>
    Image = 4,
    /// <summary>A multimodal projector (mmproj), paired with a chat or decision model.</summary>
    Projector = 5,
    /// <summary>A diffusion model's VAE, text encoder, TAESD or LoRA.</summary>
    ImageComponent = 6,
}

/// <summary>The Models page's filter tabs: each <see cref="ModelType"/> files under one.</summary>
public enum ModelTypeCategory
{
    Chat,
    Embedding,
    Decision,
    Image,
}

/// <summary>
/// Classifies a <see cref="LocalModel"/> from its GGUF metadata and file name. The signals are the
/// ones llama.cpp itself keys off: <c>general.architecture = clip</c> for a projector,
/// <c>{arch}.decision.type</c> for a decision model, <c>{arch}.pooling_type</c> for an
/// embedding/reranking model — plus <see cref="DiffusionModelGuide"/>'s file roles for the files
/// stable-diffusion.cpp loads.
/// </summary>
public static class ModelKindClassifier
{
    /// <summary>
    /// llama.cpp's decision types whose model also generates text (llama-server reports
    /// <c>["text", "decisions"]</c>); every other decision model only answers <c>/v1/systemone</c>.
    /// </summary>
    private static readonly HashSet<string> TextCapableDecisionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "granite-switch",
    };

    private static readonly HashSet<string> DiffusionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors", ".sft", ".ckpt", ".pt", ".pth",
    };

    // llama.cpp's llama_pooling_type: NONE = 0, MEAN = 1, CLS = 2, LAST = 3, RANK = 4.
    private const int PoolingRank = 4;

    public static ModelType Classify(LocalModel model)
    {
        // A checkpoint folder is a language model for TensorFold / vLLM; its config has no GGUF signals.
        if (model.Format == ModelFormat.Checkpoint)
            return ModelType.Chat;

        var fileName = Path.GetFileName(model.FilePath);

        // llama.cpp's mmproj converter always writes general.architecture = "clip"; the filename
        // check catches sibling files whose metadata hasn't been (re-)read yet.
        if (string.Equals(model.Architecture, "clip", StringComparison.OrdinalIgnoreCase)
            || ContainsToken(fileName, "mmproj"))
            return ModelType.Projector;

        if (!string.IsNullOrWhiteSpace(model.DecisionType))
            return ModelType.Decision;

        // llama.cpp only loads GGUF, so any other weights file is stable-diffusion.cpp's; a GGUF
        // is when its architecture is a diffusion one (city96's converter).
        var isDiffusion = DiffusionExtensions.Contains(Path.GetExtension(fileName))
                          || DiffusionModelGuide.IsDiffusionArchitecture(model.Architecture);
        var role = DiffusionModelGuide.ClassifyFile(model);
        if (isDiffusion)
            return role == DiffusionFileRole.Model ? ModelType.Image : ModelType.ImageComponent;

        // A GGUF text encoder or VAE for a diffusion model (e.g. city96's t5-v1_1-xxl GGUF, or a
        // Qwen LLM under a diffusers text_encoder/ folder) has an LLM architecture but isn't
        // meant to be chatted with. LoRA stays out: a llama.cpp LoRA adapter is named alike.
        if (role is DiffusionFileRole.Vae or DiffusionFileRole.ClipL or DiffusionFileRole.ClipG
                 or DiffusionFileRole.T5xxl or DiffusionFileRole.Taesd or DiffusionFileRole.TextEncoder
                 or DiffusionFileRole.Llm)
            return ModelType.ImageComponent;

        return model.PoolingType switch
        {
            PoolingRank => ModelType.Reranker,
            > 0 => ModelType.Embedding,
            _ => ModelType.Chat,
        };
    }

    /// <summary>Companion files that aren't run on their own and nest under the model they go with.</summary>
    public static bool IsAuxiliary(ModelType type) => type is ModelType.Projector or ModelType.ImageComponent;

    public static ModelTypeCategory CategoryOf(ModelType type) => type switch
    {
        ModelType.Embedding or ModelType.Reranker => ModelTypeCategory.Embedding,
        ModelType.Decision => ModelTypeCategory.Decision,
        ModelType.Image or ModelType.ImageComponent => ModelTypeCategory.Image,
        _ => ModelTypeCategory.Chat,
    };

    /// <summary>
    /// True when a model with this <c>{arch}.decision.type</c> can't generate text, so llama-server
    /// rejects chat/completions for it — such presets are left out of chat model listings.
    /// </summary>
    public static bool IsDecisionOnly(string? decisionType) =>
        !string.IsNullOrWhiteSpace(decisionType) && !TextCapableDecisionTypes.Contains(decisionType);

    public static string Label(ModelType type) => type switch
    {
        ModelType.Chat => "Chat",
        ModelType.Embedding => "Embedding",
        ModelType.Reranker => "Reranker",
        ModelType.Decision => "Decision",
        ModelType.Image => "Image",
        ModelType.Projector => "Projector",
        _ => "Component",
    };

    /// <summary>
    /// The label for one file's row: its type, made specific where that helps — which diffusion
    /// part a component is, or that a decision model also chats.
    /// </summary>
    public static string DetailedLabel(LocalModel model)
    {
        var type = Classify(model);
        if (type == ModelType.ImageComponent
            && DiffusionModelGuide.ClassifyFile(model) is var role and not (DiffusionFileRole.Model or DiffusionFileRole.Other))
            return DiffusionModelGuide.Label(role);
        if (type == ModelType.Decision && !IsDecisionOnly(model.DecisionType))
            return "Decision + Chat";
        return Label(type);
    }

    public static string Label(ModelTypeCategory category) => category switch
    {
        ModelTypeCategory.Chat => "Chat",
        ModelTypeCategory.Embedding => "Embedding & Rerank",
        ModelTypeCategory.Decision => "Decision",
        _ => "Image",
    };

    /// <summary>
    /// True if <paramref name="name"/> contains <paramref name="token"/> as its own word — not
    /// glued onto surrounding letters/digits — so "mmproj" matches "mmproj-model-F16.gguf" but not
    /// a coincidental substring inside a longer identifier. Mirrors the matching
    /// <see cref="MmprojLocator"/> uses for sibling mmproj discovery.
    /// </summary>
    private static bool ContainsToken(string? name, string token)
    {
        if (string.IsNullOrEmpty(name)) return false;

        var searchFrom = 0;
        while (true)
        {
            var idx = name.IndexOf(token, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            var leftOk = idx == 0 || !char.IsLetterOrDigit(name[idx - 1]);
            var rightIdx = idx + token.Length;
            var rightOk = rightIdx >= name.Length || !char.IsLetterOrDigit(name[rightIdx]);
            if (leftOk && rightOk) return true;

            searchFrom = idx + 1;
        }
    }
}
