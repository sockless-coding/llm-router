using System.Globalization;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// The vLLM settings a preset carries (in <see cref="ModelPreset.EngineSettings"/>) and how they become
/// <c>vllm serve</c> arguments. <see cref="ModelPreset.ModelPath"/> is a Hugging Face id (which vLLM
/// downloads itself) or a local model folder. A recipe from recipes.vllm.ai fills these in once, when
/// it's applied — a saved preset never needs the recipe site to start.
/// </summary>
public static partial class VllmPresetSettings
{
    public const string MaxModelLen = "vllm.maxModelLen";
    public const string GpuMemoryUtilization = "vllm.gpuMemoryUtilization";
    public const string Dtype = "vllm.dtype";
    public const string ToolCallParser = "vllm.toolCallParser";
    public const string AutoToolChoice = "vllm.autoToolChoice";
    public const string ReasoningParser = "vllm.reasoningParser";
    public const string TrustRemoteCode = "vllm.trustRemoteCode";
    public const string MaxConcurrentRequests = "vllm.maxConcurrentRequests";

    /// <summary>Further arguments, one token per line — one per line so a JSON value needs no shell quoting.</summary>
    public const string ExtraArgs = "vllm.extraArgs";

    /// <summary>Environment variables for the server, <c>NAME=value</c> per line.</summary>
    public const string Environment = "vllm.environment";

    // Which recipe filled the preset in: shown on the form, never read when starting the server.
    public const string RecipeId = "vllm.recipe";
    public const string RecipeHardware = "vllm.recipeHardware";
    public const string RecipeVariant = "vllm.recipeVariant";
    public const string RecipeStrategy = "vllm.recipeStrategy";
    public const string RecipeFeatures = "vllm.recipeFeatures";
    public const string RecipeSha = "vllm.recipeSha";
    public const string MinVllmVersion = "vllm.minVersion";

    /// <summary>The recipe's minimum VRAM, in GB: what the preset's memory estimate is based on.</summary>
    public const string VramGb = "vllm.vramGb";

    public static readonly IReadOnlyList<string> DtypeValues = ["auto", "bfloat16", "float16", "half", "float32", "float"];

    /// <summary>Flags the router sets itself. A recipe's are dropped; one typed into the form is an error.</summary>
    private static readonly HashSet<string> RouterOwnedFlags = new(StringComparer.Ordinal)
    {
        "--host", "--port", "--uds", "--api-key", "--served-model-name", "--config", "--model",
        "--ssl-keyfile", "--ssl-certfile", "--ssl-ca-certs", "--ssl-cert-reqs",
    };

    /// <summary>Variables a recipe doesn't get to set: they'd redirect what code the server loads, or who it authenticates as.</summary>
    private static readonly HashSet<string> ProtectedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP",
        "VIRTUAL_ENV", "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HF_ENDPOINT", "WSLENV",
    };

    /// <summary>
    /// Turns on vLLM's Rust frontend, which answers <c>/v1/messages</c> with a 501. A router preset is
    /// expected to serve every protocol, so a recipe doesn't get to switch it on; a user still can.
    /// </summary>
    public const string RustFrontendVariable = "VLLM_USE_RUST_FRONTEND";

    /// <summary>
    /// The arguments after <c>vllm</c> for <paramref name="preset"/>, without the listen address and
    /// port. <paramref name="targetIsWsl"/> converts a Windows model folder to its <c>/mnt/…</c> path.
    /// Throws <see cref="InvalidOperationException"/> for a setting vLLM wouldn't accept.
    /// </summary>
    public static List<string> ServeArgs(ModelPreset preset, bool targetIsWsl = false)
    {
        if (string.IsNullOrWhiteSpace(preset.ModelPath))
            throw new InvalidOperationException("The preset has no model — apply a recipe, or enter a Hugging Face model id.");

        var s = preset.EngineSettings;
        var model = preset.ModelPath.Trim();
        var args = new List<string>
        {
            "serve",
            targetIsWsl ? VllmLayout.ToLinuxPath(model) : model,
            "--served-model-name", VllmLayout.ServedModelName,
        };

        if (Text(s, MaxModelLen) is { } maxLen)
        {
            if (!MaxModelLenRegex().IsMatch(maxLen))
                throw new InvalidOperationException($"vLLM setting {MaxModelLen} must be a token count (32768, 32k, 1M) or auto, not '{maxLen}'.");
            args.AddRange(["--max-model-len", maxLen]);
        }
        if (Text(s, GpuMemoryUtilization) is { } gmu)
        {
            if (!double.TryParse(gmu, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) || fraction is <= 0 or > 1)
                throw new InvalidOperationException($"vLLM setting {GpuMemoryUtilization} must be a fraction above 0 and at most 1, not '{gmu}'.");
            args.AddRange(["--gpu-memory-utilization", gmu]);
        }
        if (Text(s, Dtype) is { } dtype)
        {
            if (!DtypeValues.Contains(dtype, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"vLLM setting {Dtype} must be one of {string.Join(", ", DtypeValues)}, not '{dtype}'.");
            args.AddRange(["--dtype", dtype]);
        }
        if (Flag(s, AutoToolChoice)) args.Add("--enable-auto-tool-choice");
        if (Text(s, ToolCallParser) is { } tools) args.AddRange(["--tool-call-parser", tools]);
        if (Text(s, ReasoningParser) is { } reasoning) args.AddRange(["--reasoning-parser", reasoning]);
        if (Flag(s, TrustRemoteCode)) args.Add("--trust-remote-code");

        var extra = Lines(Text(s, ExtraArgs));
        if (extra.Select(FlagName).FirstOrDefault(RouterOwnedFlags.Contains) is { } owned)
            throw new InvalidOperationException($"Remove {owned} from the extra arguments: the router sets it itself.");
        args.AddRange(extra);
        return args;
    }

    /// <summary>The preset's environment variables. Throws <see cref="InvalidOperationException"/> for a line that isn't <c>NAME=value</c>.</summary>
    public static Dictionary<string, string> EnvironmentOf(ModelPreset preset)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(Text(preset.EngineSettings, Environment)))
        {
            var separator = line.IndexOf('=');
            var name = separator > 0 ? line[..separator].Trim() : "";
            if (!EnvironmentNameRegex().IsMatch(name))
                throw new InvalidOperationException($"vLLM environment line '{line}' must look like NAME=value.");
            result[name] = line[(separator + 1)..];
        }
        return result;
    }

    /// <summary>
    /// The preset's <c>--max-model-len</c> as a token count, or null when it's unset, <c>auto</c>/<c>-1</c>
    /// or malformed. Suffixes follow vLLM: lowercase k/m are powers of 1000, uppercase K/M powers of 1024.
    /// </summary>
    public static int? MaxModelLenTokens(ModelPreset preset)
    {
        if (Text(preset.EngineSettings, MaxModelLen) is not { } raw || !MaxModelLenRegex().IsMatch(raw))
            return null;
        var multiplier = raw[^1] switch { 'k' => 1e3, 'K' => 1024d, 'm' => 1e6, 'M' => 1024d * 1024, _ => 1d };
        var number = multiplier == 1d ? raw : raw[..^1];
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return null;
        var tokens = value * multiplier;
        return tokens > int.MaxValue ? int.MaxValue : (int)tokens;
    }

    /// <summary>The server's concurrent-request limit in the router, if the preset sets one.</summary>
    public static int? MaxConcurrent(ModelPreset preset) =>
        int.TryParse(Text(preset.EngineSettings, MaxConcurrentRequests), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    /// <summary>
    /// Turns a recipe's rendered command into preset settings: what the form has a field for goes
    /// there, everything else into the extra arguments. Flags the router owns and protected environment
    /// variables are dropped, and reported in the result so the form can say so.
    /// </summary>
    public static VllmRecipePreset FromRecipe(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string> environment)
    {
        if (argv.Count < 3 || argv[0] != "vllm" || argv[1] != "serve")
            throw new InvalidOperationException($"The recipe's command isn't 'vllm serve <model>': {string.Join(' ', argv.Take(3))}");

        var settings = new Dictionary<string, string>();
        var extra = new List<string>();
        var dropped = new List<string>();
        var model = argv[2];

        // Split --flag=value up front, so the loop below only has one shape to deal with.
        var tokens = argv.Skip(3).SelectMany(t => t.StartsWith("--", StringComparison.Ordinal) && t.Contains('=')
            ? new[] { t[..t.IndexOf('=')], t[(t.IndexOf('=') + 1)..] }
            : new[] { t }).ToList();

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            bool hasValue = i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal);

            if (RouterOwnedFlags.Contains(token))
            {
                dropped.Add(token);
                while (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
                continue;
            }

            var typed = token switch
            {
                "--max-model-len" => MaxModelLen,
                "--gpu-memory-utilization" => GpuMemoryUtilization,
                "--dtype" => Dtype,
                "--tool-call-parser" => ToolCallParser,
                "--reasoning-parser" => ReasoningParser,
                _ => null,
            };
            if (typed is not null && hasValue)
            {
                settings[typed] = tokens[++i];
                continue;
            }
            if (token == "--enable-auto-tool-choice") { settings[AutoToolChoice] = "true"; continue; }
            if (token == "--trust-remote-code") { settings[TrustRemoteCode] = "true"; continue; }
            extra.Add(token);
        }

        if (extra.Count > 0)
            settings[ExtraArgs] = string.Join('\n', extra);

        var env = new List<string>();
        var droppedEnvironment = new List<string>();
        var placeholders = new List<string>();
        foreach (var (name, value) in environment)
        {
            if (name == RustFrontendVariable) continue; // Apply says so.
            if (ProtectedEnvironment.Contains(name) || !EnvironmentNameRegex().IsMatch(name)) { droppedEnvironment.Add(name); continue; }
            if (value.Contains('$')) placeholders.Add(name);
            env.Add($"{name}={value}");
        }
        if (env.Count > 0)
            settings[Environment] = string.Join('\n', env);

        return new VllmRecipePreset(model, settings, dropped, droppedEnvironment, placeholders);
    }

    /// <summary>
    /// Everything applying a recipe means for a preset: the settings, including which recipe they came
    /// from, plus what the user should read before saving. Anything the site changed from what was
    /// asked for is reported, since it can quietly mean a different machine (an RTX request answered
    /// with a CPU recipe). Throws for a recipe that needs more than one machine.
    /// </summary>
    public static VllmRecipeApplication Apply(Interfaces.VllmRecipeRendering rendering, Interfaces.VllmRecipeDetails? details, IReadOnlyList<string>? selectedFeatures, string? sourceSha = null)
    {
        if (!string.Equals(rendering.DeployType, "single_node", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"This selection deploys as '{rendering.DeployType}'. A router server is one machine, so pick a single-node strategy.");

        var converted = FromRecipe(rendering.Argv, rendering.Environment);
        var settings = new Dictionary<string, string>(converted.Settings)
        {
            [RecipeId] = rendering.HfId,
            [RecipeHardware] = rendering.Hardware,
            [RecipeVariant] = rendering.Variant,
            [RecipeStrategy] = rendering.Strategy,
        };
        if (selectedFeatures is not null) settings[RecipeFeatures] = string.Join(',', selectedFeatures);
        if (sourceSha is not null) settings[RecipeSha] = sourceSha;
        if (details?.MinVllmVersion is { } min) settings[MinVllmVersion] = min;

        var variant = details?.Variants.FirstOrDefault(v => v.Key == rendering.Variant);
        if (variant?.VramMinimumGb is { } vram) settings[VramGb] = vram.ToString(CultureInfo.InvariantCulture);

        var warnings = new List<string>();
        foreach (var a in rendering.Adjustments)
            warnings.Add($"The recipe doesn't offer {a.Param} '{a.Requested}': it used '{a.Used}' instead ({a.Reason}). Its settings were tuned for that, so check them against your hardware.");
        if (settings.ContainsKey(TrustRemoteCode))
            warnings.Add($"This recipe turns on trust-remote-code: Python code from the {converted.Model} repository will run on the server's machine.");
        if (variant?.VramMinimumGb is { } needed)
            warnings.Add($"This variant needs at least {needed.ToString(CultureInfo.InvariantCulture)} GB of VRAM.");
        if (TensorParallelSize(rendering.Argv) is > 1 and var gpus)
            warnings.Add($"This selection runs across {gpus} GPUs (--tensor-parallel-size {gpus}). Lower it in the extra arguments if this machine has fewer.");
        if (details?.Dependencies is { Length: > 0 } dependencies)
            warnings.Add($"The recipe lists extra install steps, which the router doesn't run; do them in the install's environment yourself: {dependencies}");
        if (details?.MinVllmVersion is { } minimum)
            warnings.Add($"Needs vLLM {minimum} or newer.");
        if (converted.DroppedFlags.Count > 0)
            warnings.Add($"Left out {string.Join(", ", converted.DroppedFlags)}: the router sets these itself.");
        if (rendering.Environment.ContainsKey(RustFrontendVariable))
            warnings.Add($"Left out {RustFrontendVariable}=1 from the recipe: vLLM's Rust frontend is faster, but rejects Anthropic-format requests (/v1/messages). Add it under environment variables if only OpenAI-format clients use this preset.");
        if (converted.DroppedEnvironment.Count > 0)
            warnings.Add($"Left out the environment variables {string.Join(", ", converted.DroppedEnvironment)}: a recipe can't set these.");
        if (converted.PlaceholderEnvironment.Count > 0)
            warnings.Add($"Replace the placeholder values of {string.Join(", ", converted.PlaceholderEnvironment)} with real ones.");

        return new VllmRecipeApplication(converted.Model, settings, warnings);
    }

    private static int? TensorParallelSize(IReadOnlyList<string> argv)
    {
        for (int i = 0; i + 1 < argv.Count; i++)
            if (argv[i] is "--tensor-parallel-size" or "-tp" && int.TryParse(argv[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n;
        return null;
    }

    /// <summary><c>--flag</c> of a <c>--flag=value</c> token.</summary>
    private static string FlagName(string token) => token.Contains('=') ? token[..token.IndexOf('=')] : token;

    private static List<string> Lines(string? text) =>
        text is null ? [] : text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string? Text(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    private static bool Flag(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) && value;

    [GeneratedRegex(@"^(-1|auto|\d+(\.\d+)?[kKmM]?)$")]
    private static partial Regex MaxModelLenRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentNameRegex();
}

/// <summary>A recipe ready to be filled into the preset form.</summary>
/// <param name="Model">Goes in the preset's model field.</param>
/// <param name="Settings">Go in its engine settings.</param>
/// <param name="Warnings">What the user should read before saving.</param>
public sealed record VllmRecipeApplication(string Model, IReadOnlyDictionary<string, string> Settings, IReadOnlyList<string> Warnings);

/// <summary>What a recipe's command comes to as a preset.</summary>
/// <param name="Model">The Hugging Face id it serves.</param>
/// <param name="Settings">Preset settings to fill in.</param>
/// <param name="DroppedFlags">Flags left out because the router sets them.</param>
/// <param name="DroppedEnvironment">Environment variables a recipe isn't allowed to set.</param>
/// <param name="PlaceholderEnvironment">Variables kept whose value still names something for the user to replace (<c>$IFACE_NAME</c>).</param>
public sealed record VllmRecipePreset(
    string Model,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> DroppedFlags,
    IReadOnlyList<string> DroppedEnvironment,
    IReadOnlyList<string> PlaceholderEnvironment);
