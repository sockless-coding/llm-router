using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Compiling TensorFold from source with Zig (its <c>docs/WINDOWS.md</c>, <c>docs/xpu/README.md</c>):
/// the recipe's <see cref="LlamaCppBuildRecipe.CMakeArgs"/> are the <c>zig build</c> arguments. The
/// pipeline fetches what TensorFold pins itself — the Zig toolchain named by <c>.zig-version</c>, the
/// xgrammar tag the grammar library is written against, and (XPU) oneDNN's nGEN headers — but expects
/// the GPU toolkits to be installed: the CUDA Toolkit (with MSVC on Windows), a HIP SDK, or Intel's ocloc.
/// <list type="bullet">
/// <item>CUDA: <c>-Dgpu=sm_NN</c>, one server for one GPU architecture, into <c>zig-out/native</c>.
/// <c>-Dnvcc</c> is filled in from the installed toolkit, and on Windows MSVC's <c>vcvars64.bat</c>
/// is the environment setup when the recipe names none.</item>
/// <item>HIP: <c>native-hip -Dhip=true -Dhip-arch=gfxNNNN</c>, into <c>zig-out/native-hip</c>.</item>
/// <item>XPU (Intel, Level Zero): <c>xpu-native -Dxpu-device=bmg</c>, into <c>zig-out/xpu</c>. Linux only:
/// TensorFold opens the Level Zero loader by its Linux name.</item>
/// </list>
/// The result has the release layout: <c>bin/tensorfold-native</c> beside <c>lib/</c> with the grammar library.
/// </summary>
public static partial class TensorFoldSourceBuild
{
    public const string RepoUrl = "https://github.com/ashhart/TensorFold";
    public const string XgrammarRepoUrl = "https://github.com/mlc-ai/xgrammar";
    public const string OneDnnRepoUrl = "https://github.com/uxlfoundation/oneDNN";

    /// <summary>Used when the checkout has no <c>.zig-version</c>.</summary>
    public const string DefaultZigVersion = "0.17.0";

    /// <summary>Used when the checkout doesn't name its xgrammar tag where expected.</summary>
    public const string DefaultXgrammarTag = "v0.2.8";

    /// <summary>The source pipeline for <paramref name="engine"/>: Zig for TensorFold, CMake for the ggml engines.</summary>
    public static IBuildStep[] SourcePipeline(ServerEngine engine) => engine == ServerEngine.TensorFold
        ? [new CheckTensorFoldToolsStep(), new GitSyncStep(), new EnsureZigStep(), new FetchTensorFoldDepsStep(), new ZigBuildStep(), new CollectZigOutputStep(), new FinalizeBuildStep()]
        : [new GitSyncStep(), new CMakeConfigureStep(), new CMakeBuildStep(), new CollectArtifactsStep(), new FinalizeBuildStep()];

    /// <summary>The <c>zig-out</c> folder a build of <paramref name="backend"/> installs its server into.</summary>
    public static string OutputVariant(BackendType backend) => backend switch
    {
        BackendType.Hip => "native-hip",
        BackendType.Xpu => "xpu",
        _ => "native",
    };

    /// <summary>The Zig version a checkout pins in <c>.zig-version</c>.</summary>
    public static string ZigVersionOf(string sourceDir)
    {
        var path = Path.Combine(sourceDir, ".zig-version");
        var text = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        return VersionPattern().IsMatch(text) ? text : DefaultZigVersion;
    }

    /// <summary>The xgrammar tag a checkout's grammar library is written against (<c>pinned</c> in grammar_library.zig).</summary>
    public static string XgrammarTagOf(string sourceDir)
    {
        var path = Path.Combine(sourceDir, "zig", "src", "platform", "grammar_library.zig");
        var m = File.Exists(path) ? PinnedPattern().Match(File.ReadAllText(path)) : Match.Empty;
        return m.Success ? m.Groups[1].Value : DefaultXgrammarTag;
    }

    /// <summary>ziglang.org's name for this host's toolchain build (<c>x86_64-windows</c>, <c>aarch64-linux</c>…).</summary>
    public static string ZigHostKey()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x86_64";
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        return $"{arch}-{os}";
    }

    /// <summary>
    /// The <c>zig build</c> arguments for a recipe, completed with what the router can fill in. Throws
    /// <see cref="InvalidOperationException"/> naming what's missing on this machine.
    /// </summary>
    /// <param name="args">The recipe's arguments.</param>
    /// <param name="backend">The recipe's backend.</param>
    /// <param name="find">Finds a tool (a full path, or null) — <see cref="Toolchain"/> on a real build.</param>
    /// <param name="ngenInclude">The fetched nGEN header folder (XPU).</param>
    public static List<string> CompleteArgs(IReadOnlyList<string> args, BackendType backend, IToolLocator find, string? ngenInclude, bool windows)
    {
        var result = args.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList();
        bool Has(string option) => result.Any(a => a == option || a.StartsWith(option + "=", StringComparison.Ordinal));

        switch (backend)
        {
            case BackendType.Cuda:
                if (!Has("-Dnvcc") && !Has("-Dfatbins"))
                {
                    var nvcc = find.Nvcc() ?? throw new InvalidOperationException(
                        "nvcc wasn't found. Install the NVIDIA CUDA Toolkit 13 (TensorFold's kernels are built with CUDA 13), " +
                        "or name it in the recipe with -Dnvcc=PATH.");
                    result.Add($"-Dnvcc={nvcc}");
                }
                break;

            case BackendType.Hip:
                if (!Has("-Dhipcc"))
                {
                    var hipcc = find.Hipcc() ?? throw new InvalidOperationException(
                        "hipcc wasn't found on PATH or under HIP_PATH / ROCM_PATH. Install AMD's HIP SDK (Windows) or ROCm (Linux), " +
                        "or name it in the recipe with -Dhipcc=PATH.");
                    // TensorFold looks on PATH itself; one found elsewhere has to be named.
                    if (!find.OnPath("hipcc"))
                        result.Add($"-Dhipcc={hipcc}");
                }
                break;

            case BackendType.Xpu:
                if (windows)
                    throw new InvalidOperationException(
                        "TensorFold's XPU engine opens Level Zero by its Linux name (libze_loader.so.1), so it builds and runs on Linux only. " +
                        "On Windows, build the Intel XPU image for Docker Desktop on the TensorFold tab instead; on a Linux agent with an Intel GPU this recipe works.");
                if (!Has("-Docloc"))
                {
                    var ocloc = find.Ocloc() ?? throw new InvalidOperationException(
                        "ocloc (Intel's offline compiler) wasn't found. Install intel-ocloc from Intel's GPU packages, or name it with -Docloc=PATH.");
                    result.Add($"-Docloc={ocloc}");
                }
                if (!find.OnPath("c++"))
                    throw new InvalidOperationException("The XPU build runs a C++17 compiler as 'c++' to generate its prompt kernels. Install g++ (build-essential).");
                if (!Has("-Dngen-include") && ngenInclude is not null)
                    result.Add($"-Dngen-include={ngenInclude}");
                break;
        }
        return result;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[\w.+]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"pinned\s*=\s*""(v[\w.\-]+)""")]
    private static partial Regex PinnedPattern();
}

/// <summary>Finds the GPU toolkits a TensorFold build needs. An interface so tests can stand in for the machine.</summary>
public interface IToolLocator
{
    string? Nvcc();
    string? Hipcc();
    string? Ocloc();
    bool OnPath(string tool);
}

/// <summary>Looks for toolkits where their installers put them.</summary>
public sealed class Toolchain : IToolLocator
{
    public static readonly Toolchain Instance = new();

    private static bool Win => OperatingSystem.IsWindows();

    public string? Nvcc() =>
        Which("nvcc")
        ?? UnderEnv("CUDA_PATH", "bin", Win ? "nvcc.exe" : "nvcc")
        ?? Newest(Win ? @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA" : "/usr/local", Win ? "v*" : "cuda*", Path.Combine("bin", Win ? "nvcc.exe" : "nvcc"));

    public string? Hipcc() =>
        Which("hipcc")
        ?? UnderEnv("HIP_PATH", "bin", Win ? "hipcc.exe" : "hipcc")
        ?? UnderEnv("ROCM_PATH", "bin", Win ? "hipcc.exe" : "hipcc")
        ?? (Win ? Newest(@"C:\Program Files\AMD\ROCm", "*", @"bin\hipcc.exe") : Existing("/opt/rocm/bin/hipcc"));

    public string? Ocloc() => Which("ocloc");

    public bool OnPath(string tool) => Which(tool) is not null;

    /// <summary>Whether <paramref name="tool"/> is on PATH (static form of <see cref="OnPath"/>).</summary>
    public static bool OnPathOrNull(string tool) => Which(tool) is not null;

    /// <summary>MSVC's <c>vcvars64.bat</c> (<c>vcvarsarm64.bat</c> on Arm) from the newest Visual Studio with the C++ tools, via vswhere.</summary>
    public static string? VcVars()
    {
        if (!Win) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(vswhere)
            {
                ArgumentList = { "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var path = p.StandardOutput.ReadToEnd().Trim().Split('\n').FirstOrDefault()?.Trim();
            p.WaitForExit(10_000);
            if (string.IsNullOrEmpty(path)) return null;
            var arm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
            var bat = Path.Combine(path, "VC", "Auxiliary", "Build", arm ? "vcvarsarm64.bat" : "vcvars64.bat");
            return File.Exists(bat) ? bat : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A tool on PATH (with PATHEXT on Windows), or null.</summary>
    public static string? Which(string tool)
    {
        var exts = Win ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries) : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts.Prepend(""))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), tool + ext);
                    if (File.Exists(candidate) && (ext.Length > 0 || !Win)) return candidate;
                }
                catch (ArgumentException) { /* a malformed PATH entry */ }
            }
        }
        return null;
    }

    // Machine-level too: a toolkit installed after the router started (or a service's stale environment) still counts.
    private static string? UnderEnv(string variable, params string[] parts)
    {
        var root = Environment.GetEnvironmentVariable(variable)
            ?? (Win ? Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Machine) : null);
        return string.IsNullOrWhiteSpace(root) ? null : Existing(Path.Combine([root, .. parts]));
    }

    private static string? Newest(string root, string pattern, string relative)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, pattern)
            .Select(d => Path.Combine(d, relative))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}

/// <summary>
/// Fails a build before anything is downloaded when the machine lacks a toolkit it needs (nvcc, MSVC,
/// hipcc, ocloc), naming what to install — the same checks <see cref="ZigBuildStep"/> makes.
/// </summary>
public sealed class CheckTensorFoldToolsStep : IBuildStep
{
    public string Phase => "check";

    private readonly IToolLocator _tools;

    public CheckTensorFoldToolsStep(IToolLocator? tools = null) => _tools = tools ?? Toolchain.Instance;

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var recipe = ctx.Recipe ?? throw new InvalidOperationException("CheckTensorFoldToolsStep requires a recipe.");
        var args = TensorFoldSourceBuild.CompleteArgs(recipe.CMakeArgs, ctx.BackendType, _tools, FetchTensorFoldDepsStep.NgenInclude(ctx), OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows() && ctx.BackendType == BackendType.Cuda && string.IsNullOrWhiteSpace(ctx.EnvSetupCommand) && Toolchain.VcVars() is null)
            throw new InvalidOperationException(
                "MSVC wasn't found (nvcc needs cl.exe). Install the Visual Studio Build Tools with the \"Desktop development with C++\" workload, " +
                "or set the recipe's environment setup command to your vcvars64.bat.");
        if (!Toolchain.OnPathOrNull("git"))
            throw new InvalidOperationException("git wasn't found on PATH; the build clones TensorFold and xgrammar with it.");
        await ctx.Sink.PhaseAsync(Phase, $"Toolchain found: zig build {string.Join(' ', args)}");
    }
}

/// <summary>
/// Puts the Zig toolchain the checkout pins (<c>.zig-version</c>) in the shared tools folder, downloaded
/// from ziglang.org and checked against the SHA-256 its index publishes. Kept for later builds.
/// </summary>
public sealed class EnsureZigStep : IBuildStep
{
    public string Phase => "zig";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("llm-router");
        return http;
    }

    /// <summary>Set by the step: the <c>zig</c> executable the later steps run.</summary>
    public static string ZigPath(BuildContext ctx) =>
        Path.Combine(ctx.ToolsDir, $"zig-{TensorFoldSourceBuild.ZigVersionOf(ctx.SourceDir)}", OperatingSystem.IsWindows() ? "zig.exe" : "zig");

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var version = TensorFoldSourceBuild.ZigVersionOf(ctx.SourceDir);
        var zig = ZigPath(ctx);
        if (File.Exists(zig))
        {
            await ctx.Sink.PhaseAsync(Phase, $"Zig {version} is ready ({zig}).");
            return;
        }

        var key = TensorFoldSourceBuild.ZigHostKey();
        await ctx.Sink.PhaseAsync(Phase, $"Downloading Zig {version} for {key}…");
        Directory.CreateDirectory(ctx.ToolsDir);

        string url, sha;
        using (var index = JsonDocument.Parse(await Http.GetStringAsync("https://ziglang.org/download/index.json", ct)))
        {
            if (!index.RootElement.TryGetProperty(version, out var release) || !release.TryGetProperty(key, out var build))
                throw new InvalidOperationException($"ziglang.org publishes no Zig {version} for {key}.");
            url = build.GetProperty("tarball").GetString()!;
            sha = build.GetProperty("shasum").GetString()!;
        }

        var archive = Path.Combine(ctx.ToolsDir, Path.GetFileName(new Uri(url).LocalPath));
        await using (var source = await Http.GetStreamAsync(url, ct))
        await using (var file = File.Create(archive))
            await source.CopyToAsync(file, ct);

        await using (var check = File.OpenRead(archive))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
            if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(archive);
                throw new InvalidOperationException($"The Zig download's SHA-256 is {actual}, not the {sha} ziglang.org publishes.");
            }
        }

        var staging = Path.Combine(ctx.ToolsDir, $".zig-{version}-extract");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, staging);
        }
        else
        {
            var result = await ProcessRunner.RunAsync("tar", ["-xJf", archive, "-C", staging], ctx.ToolsDir, null, line => ctx.Sink.LineAsync(Phase, line), ct);
            if (result.Cancelled) throw new OperationCanceledException(ct);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Unpacking {archive} failed (tar exit code {result.ExitCode}).");
        }

        // The archive holds one folder, zig-<key>-<version>.
        var inner = Directory.GetDirectories(staging).SingleOrDefault() ?? staging;
        var final = Path.GetDirectoryName(zig)!;
        if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
        Directory.Move(inner, final);
        TryDelete(staging);
        TryDelete(archive);

        if (!File.Exists(zig))
            throw new InvalidOperationException($"The Zig {version} archive didn't contain {Path.GetFileName(zig)}.");
        await ctx.Sink.PhaseAsync(Phase, $"Zig {version} installed in {final}.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Checks out what the build needs beside TensorFold, into the shared tools folder: the xgrammar tag
/// its grammar library is written against, and for an XPU build oneDNN's nGEN headers.
/// </summary>
public sealed class FetchTensorFoldDepsStep : IBuildStep
{
    public string Phase => "deps";

    public static string XgrammarDir(BuildContext ctx) =>
        Path.Combine(ctx.ToolsDir, $"xgrammar-{TensorFoldSourceBuild.XgrammarTagOf(ctx.SourceDir)}");

    public static string OneDnnDir(BuildContext ctx) => Path.Combine(ctx.ToolsDir, "oneDNN");

    public static string NgenInclude(BuildContext ctx) => Path.Combine(OneDnnDir(ctx), "third_party", "ngen");

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(ctx.ToolsDir);

        var tag = TensorFoldSourceBuild.XgrammarTagOf(ctx.SourceDir);
        var xgrammar = XgrammarDir(ctx);
        if (!Directory.Exists(Path.Combine(xgrammar, ".git")))
        {
            await ctx.Sink.PhaseAsync(Phase, $"Cloning xgrammar {tag} for the grammar library (structured output, required tool calls)…");
            if (Directory.Exists(xgrammar)) Directory.Delete(xgrammar, recursive: true);
            await GitAsync(ctx, ct, ctx.ToolsDir, "clone", "--depth", "1", "--branch", tag, "--recurse-submodules", "--shallow-submodules",
                TensorFoldSourceBuild.XgrammarRepoUrl, xgrammar);
        }

        if (ctx.BackendType == BackendType.Xpu && !File.Exists(Path.Combine(NgenInclude(ctx), "ngen.hpp")))
        {
            await ctx.Sink.PhaseAsync(Phase, "Fetching oneDNN's nGEN headers (the XPU prompt kernels are generated with them)…");
            var onednn = OneDnnDir(ctx);
            if (Directory.Exists(onednn)) Directory.Delete(onednn, recursive: true);
            await GitAsync(ctx, ct, ctx.ToolsDir, "clone", "--depth", "1", "--filter=blob:none", "--sparse", TensorFoldSourceBuild.OneDnnRepoUrl, onednn);
            await GitAsync(ctx, ct, onednn, "sparse-checkout", "set", "third_party/ngen");
        }
    }

    private async Task GitAsync(BuildContext ctx, CancellationToken ct, string cwd, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, cwd, null, line => ctx.Sink.LineAsync(Phase, line), ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args.Take(2))} failed with exit code {result.ExitCode}.");
    }
}

/// <summary>
/// Runs <c>zig build</c> with the recipe's arguments into the work root's build folder, then builds the
/// grammar library. A grammar build that fails only costs structured output, so it's logged, not fatal.
/// </summary>
public sealed class ZigBuildStep : IBuildStep
{
    public string Phase => "zig-build";

    private readonly IToolLocator _tools;

    public ZigBuildStep(IToolLocator? tools = null) => _tools = tools ?? Toolchain.Instance;

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var recipe = ctx.Recipe ?? throw new InvalidOperationException("ZigBuildStep requires a recipe.");
        var zig = EnsureZigStep.ZigPath(ctx);

        var args = TensorFoldSourceBuild.CompleteArgs(recipe.CMakeArgs, ctx.BackendType, _tools,
            ctx.BackendType == BackendType.Xpu ? FetchTensorFoldDepsStep.NgenInclude(ctx) : null, OperatingSystem.IsWindows());

        // nvcc needs MSVC's cl.exe as its host compiler on Windows.
        var envSetup = ctx.EnvSetupCommand;
        if (string.IsNullOrWhiteSpace(envSetup) && OperatingSystem.IsWindows() && ctx.BackendType == BackendType.Cuda)
        {
            var vcvars = Toolchain.VcVars() ?? throw new InvalidOperationException(
                "MSVC wasn't found (nvcc needs cl.exe). Install the Visual Studio Build Tools with the \"Desktop development with C++\" workload, " +
                "or set the recipe's environment setup command to your vcvars64.bat.");
            envSetup = $"\"{vcvars}\"";
            await ctx.Sink.PhaseAsync(Phase, $"Using MSVC from {vcvars}.");
        }

        if (Directory.Exists(ctx.BuildDir)) Directory.Delete(ctx.BuildDir, recursive: true);
        Directory.CreateDirectory(ctx.BuildDir);

        List<string> build = ["build", .. args, "--prefix", ctx.BuildDir];
        await ctx.Sink.PhaseAsync(Phase, $"zig {string.Join(' ', build)} — compiling the kernels can take a long time…");
        var result = await ProcessRunner.RunAsync(zig, build, ctx.SourceDir, envSetup, line => ctx.Sink.LineAsync(Phase, line), ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"zig build failed with exit code {result.ExitCode}. The build log has the compiler's output.");

        List<string> grammar = ["build", "grammar", $"-Dxgrammar={FetchTensorFoldDepsStep.XgrammarDir(ctx)}", "--prefix", ctx.BuildDir];
        await ctx.Sink.PhaseAsync(Phase, "Building the grammar library (structured output and required tool calls)…");
        var g = await ProcessRunner.RunAsync(zig, grammar, ctx.SourceDir, envSetup, line => ctx.Sink.LineAsync(Phase, line), ct);
        if (g.Cancelled) throw new OperationCanceledException(ct);
        if (g.ExitCode != 0)
            await ctx.Sink.PhaseAsync(Phase, $"The grammar library didn't build (exit code {g.ExitCode}); the server still runs, but structured output and required tool calls will be refused.");
    }
}

/// <summary>
/// Assembles the install in the release layout: the variant's <c>bin/</c> (and anything beside it),
/// the grammar library in <c>lib/</c> (which only the CUDA variant gets installed beside it), the
/// licenses and a <c>VERSION</c> file.
/// </summary>
public sealed class CollectZigOutputStep : IBuildStep
{
    public string Phase => "collect";

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var variant = TensorFoldSourceBuild.OutputVariant(ctx.BackendType);
        var built = Path.Combine(ctx.BuildDir, variant);
        var exe = Path.Combine(built, TensorFoldLayout.BinFolder, TensorFoldLayout.ExecutableName(OperatingSystem.IsWindows()));
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                $"The build produced no {Path.GetRelativePath(ctx.BuildDir, exe)}. A {ctx.BackendType} recipe must build the server into zig-out/{variant} " +
                $"(CUDA: -Dgpu=sm_NN; HIP: native-hip; XPU: xpu-native).");

        Directory.CreateDirectory(ctx.OutputDir);
        var copied = Copy(built, ctx.OutputDir);

        var grammarLib = Path.Combine(ctx.BuildDir, "native", "lib");
        if (variant != "native" && Directory.Exists(grammarLib))
            copied += Copy(grammarLib, Path.Combine(ctx.OutputDir, "lib"));

        foreach (var name in new[] { "LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md" })
        {
            var source = Path.Combine(ctx.SourceDir, name);
            if (File.Exists(source)) { File.Copy(source, Path.Combine(ctx.OutputDir, name), overwrite: true); copied++; }
        }
        if (ctx.ResolvedVersionTag is { } version)
            await File.WriteAllTextAsync(Path.Combine(ctx.OutputDir, "VERSION"), version + "\n", ct);

        await ctx.Sink.PhaseAsync(Phase, $"Copied {copied} file(s) to {ctx.OutputDir}.");
    }

    private static int Copy(string source, string dest)
    {
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            n++;
        }
        return n;
    }
}
