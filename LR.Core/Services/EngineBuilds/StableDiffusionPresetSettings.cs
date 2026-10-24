using System.Globalization;
using System.Text;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// The stable-diffusion.cpp settings a preset can carry (in <see cref="ModelPreset.EngineSettings"/>)
/// and how they become <c>sd-server</c> arguments. The preset's <see cref="ModelPreset.ModelPath"/> is
/// the main model — a full checkpoint (<c>-m</c>, SD 1.x/2.x/SDXL) or a standalone diffusion model
/// (<c>--diffusion-model</c>, Flux/SD3/Qwen-Image/Z-Image…) paired with a VAE and text encoders. The
/// generation settings are the server's defaults: a request's own size/steps/seed/… still win.
/// A setting left unset takes stable-diffusion.cpp's own default.
/// </summary>
public static class StableDiffusionPresetSettings
{
    // --- model files ---
    public const string ModelRole = "sd.modelRole";
    public const string Vae = "sd.vae";
    public const string ClipL = "sd.clipL";
    public const string ClipG = "sd.clipG";
    public const string T5xxl = "sd.t5xxl";
    public const string Llm = "sd.llm";
    public const string Taesd = "sd.taesd";
    public const string LoraDir = "sd.loraDir";

    // --- generation defaults ---
    public const string Width = "sd.width";
    public const string Height = "sd.height";
    public const string Steps = "sd.steps";
    public const string CfgScale = "sd.cfgScale";
    public const string Guidance = "sd.guidance";
    public const string SamplingMethod = "sd.samplingMethod";
    public const string Scheduler = "sd.scheduler";
    public const string Seed = "sd.seed";
    public const string ClipSkip = "sd.clipSkip";
    public const string FlowShift = "sd.flowShift";
    public const string NegativePrompt = "sd.negativePrompt";

    // --- performance ---
    public const string WeightType = "sd.type";
    public const string Threads = "sd.threads";
    public const string FlashAttention = "sd.flashAttn";
    public const string OffloadToCpu = "sd.offloadToCpu";
    public const string ClipOnCpu = "sd.clipOnCpu";
    public const string VaeOnCpu = "sd.vaeOnCpu";
    public const string VaeTiling = "sd.vaeTiling";
    public const string Mmap = "sd.mmap";
    public const string ExtraArgs = "sd.extraArgs";

    /// <summary>How <see cref="ModelPreset.ModelPath"/> is passed: decided from the other files, or forced.</summary>
    public const string RoleAuto = "auto";
    public const string RoleCheckpoint = "checkpoint";
    public const string RoleDiffusion = "diffusion";
    public static readonly IReadOnlyList<string> ModelRoleValues = [RoleAuto, RoleCheckpoint, RoleDiffusion];

    public static readonly IReadOnlyList<string> FlashAttentionValues = ["auto", "on", "off"];

    /// <summary>Suggestions for the free-text fields — stable-diffusion.cpp accepts more than these.</summary>
    public static readonly IReadOnlyList<string> SuggestedSamplingMethods =
        ["euler", "euler_a", "heun", "dpm2", "dpm++2s_a", "dpm++2m", "dpm++2mv2", "dpm++2m_sde", "ipndm", "ipndm_v", "lcm", "ddim_trailing", "tcd", "res_multistep", "res_2s", "er_sde"];
    public static readonly IReadOnlyList<string> SuggestedSchedulers =
        ["discrete", "karras", "exponential", "ays", "gits", "smoothstep", "sgm_uniform", "simple", "lcm"];
    public static readonly IReadOnlyList<string> SuggestedWeightTypes =
        ["f32", "f16", "bf16", "q8_0", "q6_K", "q5_K", "q5_1", "q5_0", "q4_K", "q4_1", "q4_0", "q3_K", "q2_K"];
    public static readonly IReadOnlyList<int> SuggestedSizes = [512, 768, 1024, 1280, 1536];

    /// <summary>The companion model files (VAE, text encoders, TAESD), keyed by setting, in argument order.</summary>
    public static readonly IReadOnlyList<(string Key, string Flag, string Label)> ComponentFiles =
    [
        (Vae, "--vae", "VAE"),
        (ClipL, "--clip_l", "CLIP-L text encoder"),
        (ClipG, "--clip_g", "CLIP-G text encoder"),
        (T5xxl, "--t5xxl", "T5-XXL text encoder"),
        (Llm, "--llm", "LLM text encoder"),
        (Taesd, "--taesd", "TAESD (fast preview decoder)"),
    ];

    /// <summary>Every model file the preset loads: the main model plus each companion file that's set.</summary>
    public static IEnumerable<string> ModelFiles(ModelPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.ModelPath))
            yield return preset.ModelPath;
        foreach (var (key, _, _) in ComponentFiles)
            if (preset.EngineSettings.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path))
                yield return path.Trim();
    }

    /// <summary>
    /// Whether <see cref="ModelPreset.ModelPath"/> is a standalone diffusion model rather than a full
    /// checkpoint. Auto: yes as soon as a separate VAE or text encoder is configured, since a full
    /// checkpoint carries its own.
    /// </summary>
    public static bool IsStandaloneDiffusionModel(ModelPreset preset)
    {
        var role = OneOf(preset.EngineSettings, ModelRole, ModelRoleValues) ?? RoleAuto;
        if (role == RoleAuto)
            return ComponentFiles.Any(c => c.Key != Taesd && Has(preset.EngineSettings, c.Key));
        return role == RoleDiffusion;
    }

    /// <summary>
    /// The <c>sd-server</c> arguments for <paramref name="preset"/> (without the listen address/port).
    /// Throws <see cref="InvalidOperationException"/> for a setting stable-diffusion.cpp wouldn't accept.
    /// </summary>
    public static List<string> ServerArgs(ModelPreset preset)
    {
        var s = preset.EngineSettings;
        var args = new List<string>();

        if (string.IsNullOrWhiteSpace(preset.ModelPath))
            throw new InvalidOperationException("The preset has no model — pick a diffusion model or checkpoint from the model library.");
        args.AddRange([IsStandaloneDiffusionModel(preset) ? "--diffusion-model" : "-m", preset.ModelPath]);

        foreach (var (key, flag, _) in ComponentFiles)
            if (Text(s, key) is { } path) args.AddRange([flag, path]);
        if (Text(s, LoraDir) is { } loraDir) args.AddRange(["--lora-model-dir", loraDir]);

        if (Int(s, Width, min: 64) is { } width) args.AddRange(["-W", Str(width)]);
        if (Int(s, Height, min: 64) is { } height) args.AddRange(["-H", Str(height)]);
        if (Int(s, Steps, min: 1) is { } steps) args.AddRange(["--steps", Str(steps)]);
        if (Float(s, CfgScale) is { } cfg) args.AddRange(["--cfg-scale", Str(cfg)]);
        if (Float(s, Guidance) is { } guidance) args.AddRange(["--guidance", Str(guidance)]);
        if (Text(s, SamplingMethod) is { } sampler) args.AddRange(["--sampling-method", sampler]);
        if (Text(s, Scheduler) is { } scheduler) args.AddRange(["--scheduler", scheduler]);
        if (Long(s, Seed) is { } seed) args.AddRange(["-s", seed.ToString(CultureInfo.InvariantCulture)]);
        if (Int(s, ClipSkip, min: -1) is { } clipSkip) args.AddRange(["--clip-skip", Str(clipSkip)]);
        if (Float(s, FlowShift) is { } flowShift) args.AddRange(["--flow-shift", Str(flowShift)]);
        if (Text(s, NegativePrompt) is { } negative) args.AddRange(["-n", negative]);

        if (Text(s, WeightType) is { } type) args.AddRange(["--type", type]);
        if (Int(s, Threads, min: -1) is { } threads) args.AddRange(["-t", Str(threads)]);
        if (OneOf(s, FlashAttention, FlashAttentionValues) is { } fa) args.AddRange(["--flash-attn", fa]);
        if (Flag(s, OffloadToCpu)) args.Add("--offload-to-cpu");
        if (Flag(s, ClipOnCpu)) args.Add("--clip-on-cpu");
        if (Flag(s, VaeOnCpu)) args.Add("--vae-on-cpu");
        if (Flag(s, VaeTiling)) args.Add("--vae-tiling");
        if (Flag(s, Mmap)) args.Add("--mmap");

        if (Text(s, ExtraArgs) is { } extra) args.AddRange(SplitArgs(extra));
        return args;
    }

    /// <summary>Splits a command-line fragment on whitespace, keeping double-quoted runs together.</summary>
    public static List<string> SplitArgs(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, any = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Str(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool Has(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw);

    private static string? Text(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    private static bool Flag(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) && value;

    private static int? Int(IReadOnlyDictionary<string, string> settings, string key, int min)
    {
        if (Text(settings, key) is not { } raw)
            return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min)
            throw new InvalidOperationException($"stable-diffusion.cpp setting {key} must be a whole number of at least {min}, not '{raw}'.");
        return value;
    }

    private static long? Long(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (Text(settings, key) is not { } raw)
            return null;
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new InvalidOperationException($"stable-diffusion.cpp setting {key} must be a whole number, not '{raw}'.");
        return value;
    }

    private static double? Float(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (Text(settings, key) is not { } raw)
            return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException($"stable-diffusion.cpp setting {key} must be a number of at least 0, not '{raw}'.");
        return value;
    }

    private static string? OneOf(IReadOnlyDictionary<string, string> settings, string key, IReadOnlyList<string> allowed)
    {
        if (Text(settings, key) is not { } raw)
            return null;
        return allowed.FirstOrDefault(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"stable-diffusion.cpp setting {key} must be one of {string.Join(", ", allowed)}, not '{raw}'.");
    }
}
