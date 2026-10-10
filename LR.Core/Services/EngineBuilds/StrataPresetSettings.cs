using System.Globalization;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// The Strata settings a preset can carry (in <see cref="ModelPreset.EngineSettings"/>) and how they
/// become arguments of Strata's <c>setup.py</c>, which prepares the preset's model and writes its
/// tuned run config. A setting left unset takes Strata's own recommendation for this PC.
/// </summary>
public static class StrataPresetSettings
{
    public const string Context = "strata.context";
    public const string Gpu = "strata.gpu";
    public const string Vision = "strata.vision";
    public const string Kv = "strata.kv";
    public const string LowRam = "strata.lowRam";
    public const string Parallel = "strata.parallel";

    /// <summary>The context lengths Strata's setup offers (others work too; past 262144 it adds rope scaling).</summary>
    public static readonly IReadOnlyList<int> SuggestedContexts = [8192, 32768, 65536, 131072, 204800, 262144, 393216, 524288];

    public static readonly IReadOnlyList<string> VisionValues = ["none", "gpu", "cpu"];
    public static readonly IReadOnlyList<string> KvValues = ["int8", "q4_0", "k8v4"];
    public static readonly IReadOnlyList<string> LowRamValues = ["auto", "on", "off", "resident", "mmap"];

    /// <summary>
    /// The <c>setup.py</c> arguments that prepare <paramref name="preset"/>'s model with the installed
    /// engine <paramref name="engine"/>: never asks (<c>--yes</c>), never starts the model or a browser.
    /// Throws <see cref="InvalidOperationException"/> for a setting Strata wouldn't accept.
    /// </summary>
    public static List<string> SetupArgs(ModelPreset preset, StrataEngineVariant? engine)
    {
        var args = new List<string> { "--yes", "--no-start", "--no-browser" };
        args.AddRange(StrataLayout.SetupFlags(engine));

        var s = preset.EngineSettings;
        if (Int(s, Context, min: 1024) is { } ctx) args.AddRange(["--context", Str(ctx)]);
        if (Int(s, Gpu, min: 0) is { } gpu) args.AddRange(["--gpu", Str(gpu)]);
        if (OneOf(s, Vision, VisionValues) is { } vision) args.AddRange(["--vision", vision]);
        if (OneOf(s, Kv, KvValues) is { } kv) args.AddRange(["--kv", kv]);
        if (OneOf(s, LowRam, LowRamValues) is { } lowRam) args.AddRange(["--low-ram", lowRam]);
        if (Int(s, Parallel, min: 1) is { } parallel) args.AddRange(["--parallel", Str(parallel)]);
        return args;
    }

    private static string Str(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int? Int(IReadOnlyDictionary<string, string> settings, string key, int min)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min)
            throw new InvalidOperationException($"Strata setting {key} must be a whole number of at least {min}, not '{raw}'.");
        return value;
    }

    private static string? OneOf(IReadOnlyDictionary<string, string> settings, string key, IReadOnlyList<string> allowed)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;
        return allowed.FirstOrDefault(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Strata setting {key} must be one of {string.Join(", ", allowed)}, not '{raw}'.");
    }
}
