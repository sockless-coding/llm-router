using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// What role a registered GGUF file plays. Most are runnable as a standalone chat model, but a
/// multimodal projector (mmproj) only makes sense paired with a text model and shouldn't be
/// presented to the user as its own top-level model.
/// </summary>
/// <remarks>
/// A draft/MTP head for self-speculative decoding was previously guessed from a "mtp" substring
/// in the filename, but that's not reliable: some repos embed the MTP head directly in the main
/// quant file and still put "MTP" in that file's name (e.g. "Qwopus3.6-27B-v2-MTP-Q6_K.gguf" is
/// the full model, not a sidecar), so the filename alone can't tell the two apart. Unlike mmproj,
/// there's no GGUF metadata field that reliably distinguishes a real standalone MTP-head sidecar
/// file either, so that guess was dropped rather than replaced with another unverified heuristic.
/// </remarks>
public enum ModelFileKind
{
    Primary = 0,
    MultimodalProjector = 1,
}

/// <summary>
/// Classifies a <see cref="LocalModel"/> file as a primary model or an auxiliary kind, from its
/// GGUF architecture and filename — there's no dedicated GGUF metadata field for this.
/// </summary>
public static class ModelKindClassifier
{
    public static ModelFileKind Classify(LocalModel model)
    {
        var fileName = Path.GetFileName(model.FilePath);

        // llama.cpp's mmproj converter always writes general.architecture = "clip"; the filename
        // check catches sibling files whose metadata hasn't been (re-)read yet.
        if (string.Equals(model.Architecture, "clip", StringComparison.OrdinalIgnoreCase)
            || ContainsToken(fileName, "mmproj"))
            return ModelFileKind.MultimodalProjector;

        return ModelFileKind.Primary;
    }

    public static string Label(ModelFileKind kind) => kind switch
    {
        ModelFileKind.MultimodalProjector => "Projector",
        _ => "Model",
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
