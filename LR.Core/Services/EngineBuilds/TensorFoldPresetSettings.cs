using System.Globalization;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// The TensorFold settings a preset carries (in <see cref="ModelPreset.EngineSettings"/>) and how they
/// become <c>tensorfold-native serve</c> arguments. <see cref="ModelPreset.ModelPath"/> is a Hugging Face
/// repo id (<c>owner/name</c>, optionally pinned with <c>@revision</c>) or a checkpoint folder on the
/// machine the server runs on. <c>serve</c> never downloads anything, so a repo id missing from that
/// machine's Hugging Face cache is pulled first (see <see cref="Pull"/>).
/// </summary>
public static class TensorFoldPresetSettings
{
    public const string Context = "tensorfold.context";
    public const string Parallel = "tensorfold.parallel";
    public const string MaxTokens = "tensorfold.maxTokens";
    public const string Thinking = "tensorfold.thinking";
    public const string ReasoningEffort = "tensorfold.reasoningEffort";
    public const string Drafter = "tensorfold.drafter";
    public const string Vision = "tensorfold.vision";

    /// <summary>GiB kept for reused prompt states (<c>--prompt-cache-gib</c>; 0 turns it off). On Intel XPU it sits in GPU memory beside the streams.</summary>
    public const string PromptCacheGib = "tensorfold.promptCacheGib";

    /// <summary>
    /// Intel's driver under WSL/Docker (WDDM) splits device allocations larger than this many MB into chunks
    /// (<c>ForceWddmHugeChunkSizeMB</c>). On some boots that chunked path aborts in <c>wddm_memory_manager.cpp</c> for any
    /// allocation over ~512 MB; 4096 keeps a model's buffers in one piece (the largest WDDM allows).
    /// </summary>
    public const string WddmChunkMb = "tensorfold.wddmChunkMb";

    /// <summary>The variable <see cref="WddmChunkMb"/> sets; Intel reads it only with <c>NEOReadDebugKeys=1</c>.</summary>
    public const string WddmChunkVariable = "ForceWddmHugeChunkSizeMB";

    /// <summary>When to run <c>tensorfold-native pull</c> before serving a repo id: <see cref="PullValues"/>.</summary>
    public const string Pull = "tensorfold.pull";

    /// <summary>Further arguments, one token per line.</summary>
    public const string ExtraArgs = "tensorfold.extraArgs";

    /// <summary>Environment variables for the server, <c>NAME=value</c> per line.</summary>
    public const string Environment = "tensorfold.environment";

    /// <summary>The device memory the model needs, in GB, for memory-group budgeting (there are no files to measure).</summary>
    public const string VramGb = "tensorfold.vramGb";

    /// <summary>The one model name every server is started under; requests are rewritten to it.</summary>
    public const string ServedModelName = "router-model";

    public const string PullMissing = "missing";
    public const string PullAlways = "always";
    public const string PullNever = "never";
    public static readonly IReadOnlyList<string> PullValues = [PullMissing, PullAlways, PullNever];

    public static readonly IReadOnlyList<string> ThinkingValues = ["on", "off"];
    public static readonly IReadOnlyList<string> ReasoningEffortValues = ["minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>In-flight limit when a preset sets no parallel count: what <c>--parallel auto</c> asks for at most.</summary>
    public const int DefaultMaxConcurrent = 8;

    /// <summary>Flags the router sets itself; typing one into the extra arguments is an error.</summary>
    private static readonly HashSet<string> RouterOwnedFlags = new(StringComparer.Ordinal)
    {
        "--host", "--port", "--unix-socket", "--name", "--alias", "--api-key", "--api-key-file",
    };

    /// <summary>Variables a preset doesn't get to set: they'd redirect what code the server loads, or who it downloads as.</summary>
    private static readonly HashSet<string> ProtectedEnvironment = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN", "HF_ENDPOINT",
    };

    /// <summary>
    /// The arguments after the executable for <paramref name="preset"/> — <c>serve</c>, the model, the
    /// served name and the preset's settings — without the listen address and port. Throws
    /// <see cref="InvalidOperationException"/> for a setting TensorFold wouldn't accept.
    /// </summary>
    public static List<string> ServeArgs(ModelPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.ModelPath))
            throw new InvalidOperationException("The preset has no model — enter a Hugging Face repo id (owner/name) or a checkpoint folder.");

        var model = preset.ModelPath.Trim();
        if (TensorFoldLayout.IsRepoId(model))
            model = TensorFoldLayout.RepoWithoutRevision(model);

        var s = preset.EngineSettings;
        var args = new List<string> { "serve", model, "--name", ServedModelName };

        if (Int(s, Context, min: 256) is { } ctx) args.AddRange(["--context", Str(ctx)]);
        if (ParallelOf(s) is { } parallel) args.AddRange(["--parallel", parallel]);
        if (Int(s, MaxTokens, min: 1) is { } maxTokens) args.AddRange(["--max-tokens", Str(maxTokens)]);
        switch (OneOf(s, Thinking, ThinkingValues))
        {
            case "on": args.Add("--thinking"); break;
            case "off": args.Add("--no-thinking"); break;
        }
        if (OneOf(s, ReasoningEffort, ReasoningEffortValues) is { } effort) args.AddRange(["--reasoning-effort", effort]);
        if (Text(s, Drafter) is { } drafter) args.AddRange(["--drafter", drafter]);
        if (Bool(s, Vision)) args.Add("--vision");
        if (Decimal(s, PromptCacheGib) is { } cacheGib) args.AddRange(["--prompt-cache-gib", cacheGib.ToString(CultureInfo.InvariantCulture)]);

        foreach (var arg in Lines(s, ExtraArgs))
        {
            var flag = arg.Split('=', 2)[0];
            if (RouterOwnedFlags.Contains(flag))
                throw new InvalidOperationException($"{flag} is set by the router; remove it from the extra arguments.");
            args.Add(arg);
        }
        return args;
    }

    /// <summary>
    /// What to pull before serving: the preset's repo id (with its revision, which <c>pull</c> pins), or
    /// null when the model is a checkpoint folder or pulling is off.
    /// </summary>
    public static string? PullRef(ModelPreset preset)
    {
        var model = preset.ModelPath?.Trim();
        if (string.IsNullOrEmpty(model) || !TensorFoldLayout.IsRepoId(model))
            return null;
        return PullMode(preset) == PullNever ? null : model;
    }

    /// <summary>The preset's pull setting, <see cref="PullMissing"/> when unset.</summary>
    public static string PullMode(ModelPreset preset) => OneOf(preset.EngineSettings, Pull, PullValues) ?? PullMissing;

    /// <summary>
    /// A POSIX shell script that pulls the model first when asked to (always, or when its Hugging Face
    /// cache folder is missing) and then <c>exec</c>s the server, so stopping the process stops the
    /// server. Run as <c>sh -c SCRIPT sh EXECUTABLE PULL_REF CACHE_FOLDER MODE SERVE_ARGS…</c>: every
    /// value arrives as a positional parameter, so nothing in it is ever parsed by the shell.
    /// </summary>
    public const string PullThenServeScript =
        "exe=\"$1\"; ref=\"$2\"; folder=\"$3\"; mode=\"$4\"; shift 4; " +
        "hub=\"${HF_HUB_CACHE:-${HF_HOME:-$HOME/.cache/huggingface}/hub}\"; " +
        "if [ \"$mode\" = always ] || [ ! -d \"$hub/$folder/snapshots\" ]; then " +
        "echo \"Pulling $ref into $hub (a first start downloads the whole checkpoint)...\"; " +
        "\"$exe\" pull \"$ref\" || exit $?; " +
        "fi; " +
        "exec \"$exe\" \"$@\"";

    /// <summary>
    /// The preset's environment variables: <see cref="WddmChunkMb"/>'s, then the typed lines (which win). Throws
    /// <see cref="InvalidOperationException"/> for a malformed or protected one.
    /// </summary>
    public static Dictionary<string, string> EnvironmentOf(ModelPreset preset)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Int(preset.EngineSettings, WddmChunkMb, min: 1) is { } chunkMb)
        {
            environment["NEOReadDebugKeys"] = "1";
            environment[WddmChunkVariable] = Str(chunkMb);
        }
        foreach (var line in Lines(preset.EngineSettings, Environment))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new InvalidOperationException($"Environment line '{line}' isn't NAME=value.");
            var name = line[..eq].Trim();
            if (ProtectedEnvironment.Contains(name))
                throw new InvalidOperationException($"{name} can't be set for a TensorFold server.");
            environment[name] = line[(eq + 1)..];
        }
        return environment;
    }

    /// <summary>The router's in-flight limit for the preset: its parallel count, else <see cref="DefaultMaxConcurrent"/>.</summary>
    public static int MaxConcurrent(ModelPreset preset)
    {
        try
        {
            return ParallelOf(preset.EngineSettings) is { } p && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : DefaultMaxConcurrent;
        }
        catch (InvalidOperationException)
        {
            return DefaultMaxConcurrent;
        }
    }

    /// <summary>The preset's <see cref="VramGb"/> in bytes, or null when unset.</summary>
    public static long? VramBytes(ModelPreset preset) =>
        preset.EngineSettings.TryGetValue(VramGb, out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) && gb > 0
            ? (long)(gb * 1024 * 1024 * 1024)
            : null;

    private static string? ParallelOf(IReadOnlyDictionary<string, string> s)
    {
        var raw = Text(s, Parallel);
        if (raw is null || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        return Int(s, Parallel, min: 1) is { } n ? Str(n) : null;
    }

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(IReadOnlyDictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : null;

    private static bool Bool(IReadOnlyDictionary<string, string> s, string key) =>
        Text(s, key) is { } raw && (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "on");

    private static IEnumerable<string> Lines(IReadOnlyDictionary<string, string> s, string key) =>
        (Text(s, key) ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    private static int? Int(IReadOnlyDictionary<string, string> s, string key, int min)
    {
        if (Text(s, key) is not { } raw)
            return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min)
            throw new InvalidOperationException($"TensorFold setting {key} must be a whole number of at least {min}, not '{raw}'.");
        return value;
    }

    private static double? Decimal(IReadOnlyDictionary<string, string> s, string key)
    {
        if (Text(s, key) is not { } raw)
            return null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || !double.IsFinite(value))
            throw new InvalidOperationException($"TensorFold setting {key} must be a number of at least 0, not '{raw}'.");
        return value;
    }

    private static string? OneOf(IReadOnlyDictionary<string, string> s, string key, IReadOnlyList<string> allowed)
    {
        if (Text(s, key) is not { } raw)
            return null;
        return allowed.FirstOrDefault(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"TensorFold setting {key} must be one of {string.Join(", ", allowed)}, not '{raw}'.");
    }
}
