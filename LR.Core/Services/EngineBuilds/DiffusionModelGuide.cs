using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>What a diffusion model file is, guessed from where it sits in its repo and its name.</summary>
public enum DiffusionFileRole
{
    /// <summary>A checkpoint or standalone diffusion model — what a preset's model is.</summary>
    Model,
    Vae,
    ClipL,
    ClipG,
    T5xxl,
    /// <summary>An LLM used as the text encoder (Qwen-Image, Z-Image).</summary>
    Llm,
    /// <summary>A text encoder whose kind the path doesn't tell (diffusers <c>text_encoder/</c>).</summary>
    TextEncoder,
    Taesd,
    Lora,
    /// <summary>Not loadable on its own: a safetensors shard, an mmproj, a safety checker…</summary>
    Other,
}

/// <summary>One file a <see cref="DiffusionFamily"/> loads: the main model or a companion.</summary>
/// <param name="SettingKey">The <see cref="StableDiffusionPresetSettings"/> key it goes in; null for the main model.</param>
/// <param name="Matches">Whether a file is this part, from <c>repo-id/path/in/repo</c> and its role.</param>
/// <param name="SourceRepo">A Hugging Face repo to get it from.</param>
/// <param name="SourceFormat">Which browser tab lists <see cref="SourceRepo"/>'s file.</param>
/// <param name="Hint">Which file to pick there.</param>
public sealed record DiffusionPart(
    string? SettingKey,
    string Label,
    bool Required,
    Func<string, DiffusionFileRole, bool> Matches,
    string SourceRepo,
    HfModelFormat SourceFormat,
    string Hint);

/// <summary>
/// A model family stable-diffusion.cpp runs, the files it needs and the generation defaults it
/// works best with (stable-diffusion.cpp's own defaults — CFG 7, no flow shift — suit SD 1.x/SDXL only).
/// </summary>
public sealed record DiffusionFamily(
    string Id,
    string Name,
    string ModelRole,
    IReadOnlyList<DiffusionPart> Parts,
    IReadOnlyDictionary<string, string> Defaults,
    string? Note = null)
{
    public DiffusionPart MainModel => Parts.First(p => p.SettingKey is null);
    public IEnumerable<DiffusionPart> Components => Parts.Where(p => p.SettingKey is not null);
}

/// <summary>A part of a repo's model family and where it already is.</summary>
public sealed record DiffusionGuidePart(DiffusionPart Part, IReadOnlyList<string> RepoFiles, LocalModel? InLibrary);

/// <summary>The companion files and defaults a preset for a diffusion model can start from.</summary>
/// <param name="Files">Setting key → library file path, for each part found in the library.</param>
/// <param name="Missing">Required parts the library doesn't have.</param>
public sealed record DiffusionPresetSuggestion(
    DiffusionFamily Family,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<DiffusionPart> Missing);

/// <summary>
/// Knows how diffusion models are split into parts — most families beyond SD 1.x/SDXL ship the
/// diffusion model, VAE and text encoders as separate files, often from different repos — so the
/// Hugging Face browser can say what to download and the preset form can wire up what's downloaded.
/// Everything is guessed from repo ids and file paths (safetensors carry no architecture metadata),
/// and only ever suggests: the user can still pick any file.
/// </summary>
public static class DiffusionModelGuide
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Separators that bound a token in a path or file name: "split_files/vae/ae.safetensors".
    private const string B = "(^|[/_.\\-])";
    private const string E = "($|[/_.\\-])";

    private static readonly Regex Shard = new(@"-\d{5}-of-\d{5}\.safetensors$", Opts);
    private static readonly Regex Mmproj = new($"{B}mmproj{E}", Opts);
    // Diffusers' unet/ and transformer/ hold only part of a checkpoint; the repo's single-file one is what loads.
    private static readonly Regex NotLoadable = new($"{B}(safety_checker|image_encoder|feature_extractor|clip_vision){E}|{B}(unet|transformer)/", Opts);
    private static readonly Regex TaesdName = new($"{B}tae(sd|f1|f2)", Opts);
    private static readonly Regex LoraName = new($"{B}loras?{E}", Opts);
    private static readonly Regex ClipLName = new($"{B}clip[-_]?l{E}", Opts);
    private static readonly Regex ClipGName = new($"{B}clip[-_]?g{E}", Opts);
    private static readonly Regex T5Name = new($"t5[-_]?xxl|umt5|{B}t5([-_]v1[._]1)?{E}", Opts);
    private static readonly Regex VaeName = new($"{B}(vae|ae){E}", Opts);
    private static readonly Regex TextEncoderDir = new($"{B}text_encoders?(_\\d)?/", Opts);
    private static readonly Regex LlmName = new("qwen|llama|gemma|mistral", Opts);
    private static readonly Regex GenericName = new(@"^(diffusion_pytorch_model|model)(\.fp16)?\.(safetensors|gguf)$", Opts);

    /// <summary>
    /// The role of <paramref name="path"/> (a repo-relative file path or a file name) in
    /// <paramref name="repoId"/>. A diffusers-style generic name (<c>diffusion_pytorch_model.safetensors</c>
    /// at a repo's root) takes the role the repo's own name gives, e.g. <c>madebyollin/taesdxl</c>.
    /// </summary>
    public static DiffusionFileRole ClassifyFile(string? repoId, string path)
    {
        var role = ClassifyPath(path.Replace('\\', '/'));
        if (role == DiffusionFileRole.Model && !string.IsNullOrEmpty(repoId) && GenericName.IsMatch(path)
            && ClassifyPath(repoId[(repoId.IndexOf('/') + 1)..]) is var repoRole and not DiffusionFileRole.Other)
            return repoRole;
        return role;
    }

    /// <summary>The role of a library file, from where it was downloaded from when known.</summary>
    public static DiffusionFileRole ClassifyFile(LocalModel model) =>
        ClassifyFile(model.HfRepoId, RepoPath(model));

    private static DiffusionFileRole ClassifyPath(string path)
    {
        if (Shard.IsMatch(path) || Mmproj.IsMatch(path) || NotLoadable.IsMatch(path)) return DiffusionFileRole.Other;
        if (TaesdName.IsMatch(path)) return DiffusionFileRole.Taesd;
        if (LoraName.IsMatch(path)) return DiffusionFileRole.Lora;
        if (ClipLName.IsMatch(path)) return DiffusionFileRole.ClipL;
        if (ClipGName.IsMatch(path)) return DiffusionFileRole.ClipG;
        if (T5Name.IsMatch(path)) return DiffusionFileRole.T5xxl;
        if (VaeName.IsMatch(Path.ChangeExtension(path, null))) return DiffusionFileRole.Vae;
        if (TextEncoderDir.IsMatch(path))
            return LlmName.IsMatch(Path.GetFileName(path)) ? DiffusionFileRole.Llm : DiffusionFileRole.TextEncoder;
        return DiffusionFileRole.Model;
    }

    public static string Label(DiffusionFileRole role) => role switch
    {
        DiffusionFileRole.Model => "Model",
        DiffusionFileRole.Vae => "VAE",
        DiffusionFileRole.ClipL => "CLIP-L",
        DiffusionFileRole.ClipG => "CLIP-G",
        DiffusionFileRole.T5xxl => "T5-XXL",
        DiffusionFileRole.Llm => "LLM text encoder",
        DiffusionFileRole.TextEncoder => "Text encoder",
        DiffusionFileRole.Taesd => "TAESD",
        DiffusionFileRole.Lora => "LoRA",
        _ => "Not loadable on its own",
    };

    // ==================== FAMILIES ====================

    private static Func<string, DiffusionFileRole, bool> Role(DiffusionFileRole role, string? nameLike = null, string? notLike = null)
    {
        var like = nameLike is null ? null : new Regex(nameLike, Opts);
        var unlike = notLike is null ? null : new Regex(notLike, Opts);
        return (path, r) => r == role && (like?.IsMatch(path) ?? true) && !(unlike?.IsMatch(path) ?? false);
    }

    // An LLM encoder is just an LLM GGUF to the classifier, so it's matched by name — specific enough
    // (size included) not to pick up a chat model from the same family.
    private static Func<string, DiffusionFileRole, bool> LlmEncoder(string nameLike)
    {
        var like = new Regex(nameLike, Opts);
        return (path, r) => r is DiffusionFileRole.Llm or DiffusionFileRole.Model or DiffusionFileRole.TextEncoder && like.IsMatch(path);
    }

    private static readonly Func<string, DiffusionFileRole, bool> AnyModel = (_, r) => r == DiffusionFileRole.Model;

    private const string Gated = " Gated: accept its license on huggingface.co and set a Hugging Face token on the Models page.";
    private const string FluxVae = @"[/_.-]ae\.|flux";

    private static readonly DiffusionPart ClipLFromFlux = new(StableDiffusionPresetSettings.ClipL, "CLIP-L text encoder", true,
        Role(DiffusionFileRole.ClipL), "comfyanonymous/flux_text_encoders", HfModelFormat.Safetensors, "clip_l.safetensors");

    private static readonly DiffusionPart T5FromFlux = new(StableDiffusionPresetSettings.T5xxl, "T5-XXL text encoder", true,
        Role(DiffusionFileRole.T5xxl, notLike: "umt5"), "comfyanonymous/flux_text_encoders", HfModelFormat.Safetensors,
        "t5xxl_fp16.safetensors, or t5xxl_fp8_e4m3fn.safetensors for half the memory.");

    /// <summary>Every family, most specific first: Z-Image and Qwen-Image before anything matching "qwen".</summary>
    public static readonly IReadOnlyList<DiffusionFamily> Families =
    [
        new("z-image", "Z-Image", StableDiffusionPresetSettings.RoleDiffusion,
        [
            new(null, "Z-Image diffusion model", true, AnyModel, "leejet/Z-Image-Turbo-GGUF", HfModelFormat.Gguf, "A quant that fits your VRAM, e.g. Q4_K or Q8_0."),
            new(StableDiffusionPresetSettings.Vae, "VAE", true, Role(DiffusionFileRole.Vae, FluxVae), "Comfy-Org/z_image_turbo", HfModelFormat.Safetensors,
                "split_files/vae/ae.safetensors — the FLUX.1 VAE, so one from a Flux download works too."),
            new(StableDiffusionPresetSettings.Llm, "Qwen3-4B text encoder", true, LlmEncoder(@"qwen[-_]?3[-_]4b"), "unsloth/Qwen3-4B-Instruct-2507-GGUF", HfModelFormat.Gguf,
                "A Qwen3-4B GGUF, e.g. Q4_K_M or Q8_0."),
        ],
        Defaults(cfg: "1.0")),

        new("qwen-image", "Qwen-Image", StableDiffusionPresetSettings.RoleDiffusion,
        [
            new(null, "Qwen-Image diffusion model", true, AnyModel, "QuantStack/Qwen-Image-GGUF", HfModelFormat.Gguf, "A quant that fits your VRAM — the model is 20B parameters, so consider offloading to CPU."),
            new(StableDiffusionPresetSettings.Vae, "VAE", true, Role(DiffusionFileRole.Vae, "qwen"), "Comfy-Org/Qwen-Image_ComfyUI", HfModelFormat.Safetensors,
                "split_files/vae/qwen_image_vae.safetensors"),
            new(StableDiffusionPresetSettings.Llm, "Qwen2.5-VL-7B text encoder", true, LlmEncoder(@"qwen[-_]?2[._]?5[-_]?vl[-_]?7b"), "unsloth/Qwen2.5-VL-7B-Instruct-GGUF", HfModelFormat.Gguf,
                "A Qwen2.5-VL-7B-Instruct GGUF, e.g. Q8_0 — not the mmproj file."),
        ],
        Defaults(cfg: "2.5", sampler: "euler", flowShift: "3")),

        new("wan", "Wan", StableDiffusionPresetSettings.RoleDiffusion,
        [
            new(null, "Wan diffusion model", true, AnyModel, "Comfy-Org/Wan_2.1_ComfyUI_repackaged", HfModelFormat.Safetensors,
                "split_files/diffusion_models/, e.g. wan2.1_t2v_1.3B_fp16.safetensors."),
            new(StableDiffusionPresetSettings.Vae, "VAE", true, Role(DiffusionFileRole.Vae, "wan"), "Comfy-Org/Wan_2.1_ComfyUI_repackaged", HfModelFormat.Safetensors,
                "split_files/vae/wan_2.1_vae.safetensors (Wan2.2 TI2V 5B uses wan2.2_vae instead)."),
            new(StableDiffusionPresetSettings.T5xxl, "UMT5-XXL text encoder", true, Role(DiffusionFileRole.T5xxl, "umt5"), "city96/umt5-xxl-encoder-gguf", HfModelFormat.Gguf,
                "A umt5-xxl-encoder GGUF, e.g. Q8_0."),
        ],
        Defaults(cfg: "6.0", sampler: "euler", flowShift: "3"),
        "Wan2.2 A14B has a second, high-noise model: pass it in extra arguments as --high-noise-diffusion-model."),

        new("flux", "FLUX.1", StableDiffusionPresetSettings.RoleDiffusion,
        [
            new(null, "FLUX.1 diffusion model", true, AnyModel, "city96/FLUX.1-dev-gguf", HfModelFormat.Gguf,
                "A quant that fits your VRAM, e.g. Q4_K_S or Q8_0. For FLUX.1-schnell (4 steps): city96/FLUX.1-schnell-gguf."),
            new(StableDiffusionPresetSettings.Vae, "VAE", true, Role(DiffusionFileRole.Vae, FluxVae), "black-forest-labs/FLUX.1-schnell", HfModelFormat.Safetensors,
                "ae.safetensors"),
            ClipLFromFlux,
            T5FromFlux,
        ],
        Defaults(cfg: "1.0", sampler: "euler")),

        new("sd3", "Stable Diffusion 3.x", StableDiffusionPresetSettings.RoleCheckpoint,
        [
            new(null, "SD3 checkpoint", true, AnyModel, "stabilityai/stable-diffusion-3.5-large", HfModelFormat.Safetensors,
                "sd3.5_large.safetensors (or the 3.5-medium repo's)." + Gated),
            ClipLFromFlux,
            new(StableDiffusionPresetSettings.ClipG, "CLIP-G text encoder", true, Role(DiffusionFileRole.ClipG), "stabilityai/stable-diffusion-3.5-large", HfModelFormat.Safetensors,
                "text_encoders/clip_g.safetensors." + Gated),
            T5FromFlux,
        ],
        Defaults(cfg: "4.5", sampler: "euler")),

        new("sdxl", "Stable Diffusion XL", StableDiffusionPresetSettings.RoleCheckpoint,
        [
            new(null, "SDXL checkpoint", true, AnyModel, "stabilityai/stable-diffusion-xl-base-1.0", HfModelFormat.Safetensors,
                "sd_xl_base_1.0.safetensors — the single file at the repo root, not the diffusers subfolders. It includes its VAE and text encoders."),
            new(StableDiffusionPresetSettings.Vae, "Fixed fp16 VAE", false, Role(DiffusionFileRole.Vae, "sdxl|sd[-_]xl"), "madebyollin/sdxl-vae-fp16-fix", HfModelFormat.Safetensors,
                "sdxl_vae.safetensors — replaces the built-in VAE, which can turn images black in fp16."),
            new(StableDiffusionPresetSettings.Taesd, "TAESDXL fast decoder", false, Role(DiffusionFileRole.Taesd, "xl"), "madebyollin/taesdxl", HfModelFormat.Safetensors,
                "diffusion_pytorch_model.safetensors — much faster, lower-quality decoding."),
        ],
        Defaults()),

        new("sd1", "Stable Diffusion 1.x / 2.x", StableDiffusionPresetSettings.RoleCheckpoint,
        [
            new(null, "SD 1.x/2.x checkpoint", true, AnyModel, "stable-diffusion-v1-5/stable-diffusion-v1-5", HfModelFormat.Safetensors,
                "v1-5-pruned-emaonly.safetensors — a single-file checkpoint including its VAE and text encoder."),
            new(StableDiffusionPresetSettings.Vae, "Improved VAE", false, Role(DiffusionFileRole.Vae, "ft[-_]?mse|ft[-_]?ema"), "stabilityai/sd-vae-ft-mse-original", HfModelFormat.Safetensors,
                "vae-ft-mse-840000-ema-pruned.safetensors — sharper faces and details than the built-in VAE."),
            new(StableDiffusionPresetSettings.Taesd, "TAESD fast decoder", false, Role(DiffusionFileRole.Taesd, notLike: "xl|sd3|f1|f2"), "madebyollin/taesd", HfModelFormat.Safetensors,
                "diffusion_pytorch_model.safetensors — much faster, lower-quality decoding."),
        ],
        Defaults()),
    ];

    private static IReadOnlyDictionary<string, string> Defaults(string? cfg = null, string? sampler = null, string? flowShift = null)
    {
        var d = new Dictionary<string, string>();
        if (cfg is not null) d[StableDiffusionPresetSettings.CfgScale] = cfg;
        if (sampler is not null) d[StableDiffusionPresetSettings.SamplingMethod] = sampler;
        if (flowShift is not null) d[StableDiffusionPresetSettings.FlowShift] = flowShift;
        return d;
    }

    private static readonly (string Family, Regex Pattern)[] FamilyPatterns =
    [
        ("z-image", new(@"z[-_ ]?image", Opts)),
        ("qwen-image", new(@"qwen[-_ ]?image", Opts)),
        ("wan", new($"{B}wan[-_ ]?2", Opts)),
        // FLUX.2 pairs with a different text encoder (Mistral), so it isn't FLUX.1.
        ("flux", new(@"flux(?![-_. ]?2)", Opts)),
        ("sd3", new($"{B}sd[-_]?3|stable[-_ ]?diffusion[-_ ]?3", Opts)),
        ("sdxl", new(@"sdxl|sd[-_]xl|stable[-_ ]?diffusion[-_ ]?xl", Opts)),
        ("sd1", new($"stable[-_ ]?diffusion[-_ ]?v?[12]{E}|{B}sd[-_]?v?(1[-_.]?5|2[-_.]?1){E}|{B}v1[-_]5{E}", Opts)),
    ];

    // general.architecture as city96's GGUF converter writes it.
    private static readonly Dictionary<string, string> FamilyByArchitecture = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flux"] = "flux", ["sd1"] = "sd1", ["sdxl"] = "sdxl", ["sd3"] = "sd3", ["wan"] = "wan", ["qwen_image"] = "qwen-image",
    };

    /// <summary>The family the first of <paramref name="hints"/> (repo id, file path…) names, if any.</summary>
    public static DiffusionFamily? DetectFamily(params string?[] hints)
    {
        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            foreach (var (id, pattern) in FamilyPatterns)
                if (pattern.IsMatch(hint))
                    return Families.First(f => f.Id == id);
        }
        return null;
    }

    /// <summary>The family of a library file: from its repo, its file name, then its GGUF architecture.</summary>
    public static DiffusionFamily? DetectFamily(LocalModel model) =>
        DetectFamily(model.HfRepoId, RepoPath(model))
        ?? (model.Architecture is { } arch && FamilyByArchitecture.TryGetValue(arch, out var id) ? Families.First(f => f.Id == id) : null);

    // ==================== HUGGING FACE BROWSER ====================

    /// <summary>
    /// What a repo's model family needs, and for each part the files of <paramref name="repoFiles"/>
    /// that are it and the library file that already is. Null when the repo isn't a known family.
    /// </summary>
    public static (DiffusionFamily Family, IReadOnlyList<DiffusionGuidePart> Parts)? ForRepo(
        string repoId, IReadOnlyList<string> repoFiles, IReadOnlyList<LocalModel> library)
    {
        var family = DetectFamily([repoId, .. repoFiles]);
        if (family is null)
            return null;

        var parts = family.Parts.Select(part =>
        {
            var inRepo = repoFiles.Where(f => Matches(part, repoId, f)).ToList();
            return new DiffusionGuidePart(part, inRepo, BestInLibrary(family, part, library, repoId, exclude: null));
        }).ToList();
        return (family, parts);
    }

    // ==================== PRESETS ====================

    /// <summary>
    /// The companion files from <paramref name="library"/> a preset running <paramref name="model"/>
    /// needs, preferring ones from the same repo. Null unless <paramref name="model"/> is the main
    /// model of a known family.
    /// </summary>
    public static DiffusionPresetSuggestion? ForPreset(LocalModel model, IReadOnlyList<LocalModel> library)
    {
        if (ClassifyFile(model) != DiffusionFileRole.Model || DetectFamily(model) is not { } family)
            return null;

        var files = new Dictionary<string, string>();
        var missing = new List<DiffusionPart>();
        foreach (var part in family.Components)
        {
            if (BestInLibrary(family, part, library, model.HfRepoId, exclude: model) is { } found)
                files[part.SettingKey!] = found.FilePath;
            else if (part.Required)
                missing.Add(part);
        }
        return new DiffusionPresetSuggestion(family, files, missing);
    }

    private static LocalModel? BestInLibrary(DiffusionFamily family, DiffusionPart part, IReadOnlyList<LocalModel> library, string? repoId, LocalModel? exclude) =>
        library
            .Where(m => m.Id != exclude?.Id && m.Status is ModelStatus.Ready or ModelStatus.Downloading)
            // The main model of a family is any model file, so it has to come from that family too.
            .Where(m => Matches(part, m.HfRepoId, RepoPath(m)) && (part.SettingKey is not null || DetectFamily(m) == family))
            .OrderByDescending(m => string.Equals(m.HfRepoId, repoId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.Status == ModelStatus.Ready)
            .ThenByDescending(m => DetectFamily(m) == family)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    // The repo id is part of what's matched: a VAE in an SDXL repo is an SDXL VAE, whatever its file is called.
    private static bool Matches(DiffusionPart part, string? repoId, string path) =>
        part.Matches($"{repoId}/{path.Replace('\\', '/')}", ClassifyFile(repoId, path));

    /// <summary>A library file's path within its repo (keeping subfolders like <c>split_files/vae/</c>), or its file name.</summary>
    private static string RepoPath(LocalModel model) =>
        !string.IsNullOrEmpty(model.HfFilename) ? model.HfFilename : Path.GetFileName(model.FilePath);
}
