namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// One tool a Strata install needs on this PC: git (to clone and update it), Python 3.10+ (for its
/// <c>.venv</c>) and, when Strata has to compile its engine, a C++ toolchain (Visual Studio Build Tools
/// on Windows). Strata's own setup offers to install the build tools, but it can't ask from a background
/// install — so they're checked up front instead.
/// </summary>
/// <param name="Name">What is needed, e.g. "Git".</param>
/// <param name="Found">True if it's installed.</param>
/// <param name="Required">True if the install can't go ahead without it; false if it's only needed later or optionally.</param>
/// <param name="Detail">What was found (version, location), or why it's needed when missing.</param>
/// <param name="InstallUrl">Where to get it.</param>
public sealed record StrataPrerequisite(string Name, bool Found, bool Required, string Detail, string InstallUrl)
{
    public bool Blocks => Required && !Found;
}

/// <summary>Finds the tools in <see cref="StrataPrerequisite"/>. Best-effort: a tool that can't be run counts as missing.</summary>
public static class StrataPrerequisites
{
    public const string GitUrl = "https://git-scm.com/downloads";
    public const string PythonUrl = "https://www.python.org/downloads/";
    public const string BuildToolsUrl = "https://visualstudio.microsoft.com/visual-cpp-build-tools/";

    /// <summary>The C++ toolchain's display name on this OS.</summary>
    public static string CompilerName => OperatingSystem.IsWindows() ? "Visual Studio Build Tools (C++)" : "C++ compiler (build-essential)";

    /// <summary><c>git --version</c>'s output, or null if git isn't on PATH.</summary>
    public static async Task<string?> FindGitAsync(CancellationToken ct)
    {
        string? version = null;
        try
        {
            var result = await ProcessRunner.RunAsync("git", ["--version"], Path.GetTempPath(), null,
                line => { if (!string.IsNullOrWhiteSpace(line)) version ??= line.Trim(); return Task.CompletedTask; }, ct);
            return result.ExitCode == 0 ? version ?? "git" : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a C++ toolchain is: on Windows, a Visual Studio / Build Tools install with the MSVC x64
    /// tools (found with <c>vswhere</c>); elsewhere, <c>c++</c> on PATH. Null if there's none.
    /// </summary>
    public static async Task<string?> FindCompilerAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var vswhere = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vswhere))
                return null;
            return await FirstLineAsync(vswhere,
                ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                 "-property", "installationPath", "-nologo"], ct);
        }

        return await FirstLineAsync("c++", ["--version"], ct);
    }

    private static async Task<string?> FirstLineAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        string? first = null;
        try
        {
            var result = await ProcessRunner.RunAsync(executable, args, Path.GetTempPath(), null,
                line => { if (!string.IsNullOrWhiteSpace(line)) first ??= line.Trim(); return Task.CompletedTask; }, ct);
            return result.ExitCode == 0 ? first : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The message an install fails with when <paramref name="checks"/> has blocking gaps, or null if none.</summary>
    public static string? MissingMessage(IEnumerable<StrataPrerequisite> checks)
    {
        var missing = checks.Where(c => c.Blocks).ToList();
        if (missing.Count == 0)
            return null;
        return "Strata needs " + string.Join(", ", missing.Select(m => $"{m.Name} ({m.InstallUrl})")) +
               ". Install " + (missing.Count == 1 ? "it" : "them") + ", then restart LLM Router so it sees the new PATH, and try again.";
    }
}
