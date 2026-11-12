using System.Text.Json;
using System.Text.Json.Serialization;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// A small file written into every finished engine install (<see cref="FileName"/>) recording what the
/// folder holds: engine, backend, how it was obtained and which version. Another router instance that
/// shares the same install root reads it to register the folder with the right details (see
/// <see cref="SharedLibraryDiscovery"/>), and to pick up an in-place update made by another instance.
/// </summary>
public sealed class EngineInstallManifest
{
    public const string FileName = ".llm-router-build.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = string.Empty;
    public ServerEngine Engine { get; set; }
    public BackendType BackendType { get; set; }
    public EngineBuildSource Source { get; set; }
    public string? VersionTag { get; set; }
    public string? CommitSha { get; set; }
    public string? TargetOs { get; set; }
    public string? TargetArch { get; set; }

    /// <summary>The compile recipe's name, for a source build. Built-in recipes have the same name on every instance.</summary>
    public string? RecipeName { get; set; }

    /// <summary>The OS (<c>win</c>/<c>ubuntu</c>/<c>macos</c>) and architecture of the machine that made the install.
    /// An instance on another kind of machine can't run it, so it doesn't register it.</summary>
    public string? HostOs { get; set; }
    public string? HostArch { get; set; }

    public DateTime? BuildCompletedAt { get; set; }

    public static EngineInstallManifest FromBuild(EngineBuild build, string? recipeName)
    {
        var (os, arch) = ReleaseAssetResolver.DetectHost();
        return new EngineInstallManifest
        {
            Name = build.Name,
            Engine = build.Engine,
            BackendType = build.BackendType,
            Source = build.Source,
            VersionTag = build.VersionTag,
            CommitSha = build.CommitSha,
            TargetOs = build.TargetOs,
            TargetArch = build.TargetArch,
            RecipeName = recipeName,
            HostOs = os,
            HostArch = arch,
            BuildCompletedAt = build.BuildCompletedAt,
        };
    }

    /// <summary>True when the install was made on a machine of this kind, or the manifest doesn't say.</summary>
    public bool RunsOnThisHost()
    {
        var (os, arch) = ReleaseAssetResolver.DetectHost();
        return (HostOs is null || string.Equals(HostOs, os, StringComparison.OrdinalIgnoreCase))
            && (HostArch is null || string.Equals(HostArch, arch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A new registry row for the install in <paramref name="installPath"/>.</summary>
    public EngineBuild ToBuild(string installPath, Guid? recipeId) => new()
    {
        Id = Guid.NewGuid(),
        Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileName(installPath) : Name,
        Engine = Engine,
        BackendType = BackendType,
        Source = Source,
        RecipeId = recipeId,
        InstallPath = installPath,
        VersionTag = VersionTag,
        CommitSha = CommitSha,
        TargetOs = TargetOs,
        TargetArch = TargetArch,
        BuildCompletedAt = BuildCompletedAt,
    };

    /// <summary>
    /// Copies what can change on disk (an in-place update by another instance) onto an existing row.
    /// Returns true if anything changed. The row's name and recipe link are left alone.
    /// </summary>
    public bool ApplyTo(EngineBuild build)
    {
        if (build.Engine != Engine)
            return false;

        var changed = false;
        void Set<T>(T current, T value, Action<T> assign)
        {
            if (value is null || EqualityComparer<T>.Default.Equals(current, value)) return;
            assign(value);
            changed = true;
        }

        Set(build.VersionTag, VersionTag, v => build.VersionTag = v);
        Set(build.CommitSha, CommitSha, v => build.CommitSha = v);
        Set(build.TargetOs, TargetOs, v => build.TargetOs = v);
        Set(build.TargetArch, TargetArch, v => build.TargetArch = v);
        Set(build.BuildCompletedAt, BuildCompletedAt, v => build.BuildCompletedAt = v);
        if (BackendType != BackendType.Unknown)
            Set(build.BackendType, BackendType, v => build.BackendType = v);
        return changed;
    }

    /// <summary>The manifest in <paramref name="installPath"/>, or null when there's none (or it can't be read).</summary>
    public static EngineInstallManifest? TryRead(string installPath)
    {
        try
        {
            var path = Path.Combine(installPath, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<EngineInstallManifest>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the manifest into <paramref name="installPath"/>. Best effort: returns false if the folder can't be written.</summary>
    public bool TryWrite(string installPath)
    {
        try
        {
            if (!Directory.Exists(installPath)) return false;
            var path = Path.Combine(installPath, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether the manifest already records what <paramref name="build"/> holds.</summary>
    public bool Matches(EngineBuild build) =>
        Engine == build.Engine && BackendType == build.BackendType && Source == build.Source &&
        VersionTag == build.VersionTag && CommitSha == build.CommitSha;
}

/// <summary>
/// What an engine install folder's name says about it, for installs made before
/// <see cref="EngineInstallManifest"/> existed. The router names its folders
/// <c>{prefix}{tag}-{backend}</c> (a release) or <c>{prefix}{ref}-{backend}-{recipe}</c> (a source build).
/// </summary>
public sealed record EngineFolderInfo(string? VersionTag, BackendType Backend, EngineBuildSource Source, string? RecipeSlug)
{
    /// <summary>The folder prefix the router uses for <paramref name="engine"/>'s installs.</summary>
    public static string PrefixOf(ServerEngine engine) => engine switch
    {
        ServerEngine.StableDiffusionCpp => "sd-",
        ServerEngine.TensorFold => "tensorfold-",
        ServerEngine.Vllm => "vllm-",
        ServerEngine.Strata => "strata-",
        _ => "",
    };

    private static readonly Dictionary<string, BackendType> BackendNames = Enum.GetValues<BackendType>()
        .Where(b => b != BackendType.Unknown)
        .ToDictionary(b => b.ToString().ToLowerInvariant(), b => b);

    public static EngineFolderInfo Parse(ServerEngine engine, string folderName)
    {
        var prefix = PrefixOf(engine);
        var rest = prefix.Length > 0 && folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? folderName[prefix.Length..]
            : folderName;
        var parts = rest.Split('-');

        // Strata: strata-{tag}-{cuda|cuda12|hip} for a release install, anything else is a checkout.
        if (engine == ServerEngine.Strata)
        {
            var tag = parts[0];
            return StrataLayout.IsReleaseTag(tag) && folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? new EngineFolderInfo(tag, BackendType.Unknown, EngineBuildSource.OfficialRelease, null)
                : new EngineFolderInfo(null, BackendType.Unknown, EngineBuildSource.GitCheckout, null);
        }

        // The first segment after the tag that names a backend; tags can hold dashes (sd.cpp's master-504-636d3cb).
        for (int i = 1; i < parts.Length; i++)
        {
            if (!BackendNames.TryGetValue(parts[i].ToLowerInvariant(), out var backend))
                continue;
            var tag = string.Join('-', parts[..i]);
            var recipe = i + 1 < parts.Length ? string.Join('-', parts[(i + 1)..]) : null;
            return recipe is null
                ? new EngineFolderInfo(tag, backend, EngineBuildSource.OfficialRelease, null)
                : new EngineFolderInfo(null, backend, EngineBuildSource.SourceCompile, recipe.ToLowerInvariant());
        }

        return new EngineFolderInfo(null, BackendType.Unknown, EngineBuildSource.OfficialRelease, null);
    }
}
