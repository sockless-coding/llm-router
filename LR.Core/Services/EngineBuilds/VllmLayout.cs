using System.Text.RegularExpressions;

namespace LR.Core.Services.EngineBuilds;

/// <summary>A folder inside a WSL distro: which distro, and the Linux path in it.</summary>
public sealed record WslLocation(string Distro, string LinuxPath)
{
    /// <summary>The same folder as Windows sees it, e.g. <c>\\wsl.localhost\Ubuntu\home\me\vllm</c>.</summary>
    public string ToUncPath() => $@"\\wsl.localhost\{Distro}{LinuxPath.Replace('/', '\\')}";
}

/// <summary>What to run for a vLLM install: the program, the arguments in front of the server's own, and the directory.</summary>
/// <param name="Executable">Program to start (<c>wsl.exe</c>, or the venv's <c>vllm</c>).</param>
/// <param name="PrefixArgs">Arguments that come before <c>serve …</c>.</param>
/// <param name="WorkingDirectory">Directory on the machine the wrapper runs on, or null when the prefix args set it (WSL).</param>
/// <param name="Wsl">The distro location when the install lives in WSL.</param>
public sealed record VllmCommand(string Executable, IReadOnlyList<string> PrefixArgs, string? WorkingDirectory, WslLocation? Wsl);

/// <summary>
/// What a vLLM install looks like on disk and how to start it. vLLM only runs on Linux, so on Windows
/// an install lives inside a WSL distro and its <see cref="Models.EngineBuild.InstallPath"/> is the
/// distro's network path (<c>\\wsl.localhost\Ubuntu\home\me\…</c>): that's a path Windows can check,
/// and it says which distro to launch in. Everything here works on strings, because the router also
/// describes installs on agents running a different OS than its own.
/// </summary>
public static partial class VllmLayout
{
    public const string Repo = "vllm-project/vllm";

    /// <summary>Name every vLLM server answers to; the provider rewrites each request's <c>model</c> to it (see the provider).</summary>
    public const string ServedModelName = "llm-router";

    /// <summary>The Python version vLLM's docs recommend, and the only one its ROCm wheels are built for.</summary>
    public const string PythonVersion = "3.12";

    /// <summary>Where managed installs go inside a WSL distro, relative to the user's home. Linux's own filesystem: a venv on <c>/mnt/…</c> is very slow.</summary>
    public const string WslInstallRoot = ".llm-router/engines";

    /// <summary>
    /// The backends a managed install can target. CPU wheels need a hand-set <c>LD_PRELOAD</c>, so they're
    /// left to a venv the user made. Intel GPUs go through Docker Desktop (see <see cref="VllmDocker"/>).
    /// </summary>
    public static IReadOnlyList<Models.BackendType> InstallBackends(bool targetIsWindows) => targetIsWindows
        ? [Models.BackendType.Cuda, Models.BackendType.Sycl]
        : [Models.BackendType.Cuda, Models.BackendType.Hip];

    /// <summary>The folder name of a managed install, which is also how a Docker one is recognised.</summary>
    public static string InstallFolderName(string tag, Models.BackendType backend) => backend == Models.BackendType.Sycl
        ? VllmDocker.FolderName
        : $"vllm-{tag}-{backend.ToString().ToLowerInvariant()}";

    /// <summary>Parses <c>\\wsl.localhost\Distro\path</c> or <c>\\wsl$\Distro\path</c>; null for anything else.</summary>
    public static WslLocation? ParseWslPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var match = WslUncRegex().Match(path.Trim().Replace('/', '\\'));
        if (!match.Success)
            return null;
        var linux = match.Groups["path"].Value.Replace('\\', '/').TrimEnd('/');
        return new WslLocation(match.Groups["distro"].Value, linux.Length == 0 ? "/" : linux);
    }

    /// <summary>
    /// The path a process inside WSL uses for <paramref name="path"/>: a distro path becomes its Linux
    /// path, <c>F:\models</c> becomes <c>/mnt/f/models</c>. Anything else (a Hugging Face id, a Linux path) is returned as is.
    /// </summary>
    public static string ToLinuxPath(string path)
    {
        if (ParseWslPath(path) is { } wsl)
            return wsl.LinuxPath;
        var drive = WindowsDriveRegex().Match(path);
        return drive.Success
            ? $"/mnt/{char.ToLowerInvariant(drive.Groups["drive"].Value[0])}/{drive.Groups["rest"].Value.Replace('\\', '/').TrimStart('/')}".TrimEnd('/')
            : path;
    }

    /// <summary>The <c>wsl.exe</c> arguments that select <paramref name="distro"/> (none for the default distro).</summary>
    public static IEnumerable<string> WslDistroArgs(string? distro) =>
        string.IsNullOrWhiteSpace(distro) ? [] : ["-d", distro];

    /// <summary>
    /// How to start the install in <paramref name="folder"/> on a machine of the given OS. Windows: a
    /// distro path runs through <c>wsl.exe</c>; any other folder is a native venv (the community
    /// vllm-windows fork). Linux: the venv's <c>vllm</c>. With no <paramref name="exists"/> check
    /// (a remote machine), the standard <c>.venv</c> layout is assumed.
    /// </summary>
    public static VllmCommand Resolve(string folder, bool targetIsWindows, Func<string, bool>? exists = null)
    {
        if (targetIsWindows && ParseWslPath(folder) is { } wsl)
        {
            var linuxExecutable = $"{wsl.LinuxPath}/.venv/bin/vllm";
            return new VllmCommand("wsl.exe", ["-d", wsl.Distro, "--cd", wsl.LinuxPath, "--exec", linuxExecutable], null, wsl);
        }

        var separator = targetIsWindows ? '\\' : '/';
        var relative = targetIsWindows
            ? new[] { @".venv\Scripts\vllm.exe", @"Scripts\vllm.exe" }
            : new[] { ".venv/bin/vllm", "bin/vllm" };
        var candidates = relative.Select(r => folder.TrimEnd('/', '\\') + separator + r).ToList();
        var executable = exists is null ? candidates[0] : candidates.FirstOrDefault(exists) ?? candidates[0];
        return new VllmCommand(executable, [], folder, null);
    }

    /// <summary>True if <paramref name="folder"/> on this machine holds a vLLM executable in a layout <see cref="Resolve"/> knows.</summary>
    public static bool IsInstall(string folder) => VllmDocker.IsDockerFolder(folder)
        ? VllmDocker.IsInstall(folder)
        : ParseWslPath(folder) is not null
            // Windows sees a distro's files through the share, under their Linux names.
            ? File.Exists(Path.Combine(folder, ".venv", "bin", "vllm"))
            : File.Exists(Resolve(folder, OperatingSystem.IsWindows(), File.Exists).Executable);

    /// <summary>True for a version pip can be asked for: <c>0.20.1</c>, <c>v0.20.1</c>, <c>0.20.1rc1</c>.</summary>
    public static bool IsVersion(string version) => VersionRegex().IsMatch(version);

    /// <summary>Accepts <c>v0.20.1</c> or <c>0.20.1</c>; returns the version pip wants, without the <c>v</c>.</summary>
    public static string NormalizeVersion(string version)
    {
        var trimmed = version.Trim();
        if (!IsVersion(trimmed))
            throw new InvalidOperationException($"'{version}' isn't a vLLM version (like 0.20.1).");
        return trimmed.TrimStart('v', 'V');
    }

    [GeneratedRegex(@"^\\\\(?:wsl\.localhost|wsl\$)\\(?<distro>[^\\]+)(?<path>(?:\\.*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex WslUncRegex();

    [GeneratedRegex(@"^(?<drive>[A-Za-z]):[\\/](?<rest>.*)$")]
    private static partial Regex WindowsDriveRegex();

    [GeneratedRegex(@"^[vV]?\d+(\.\d+){1,3}([a-z]+\d*)?(\.[a-z]+\d*)?$")]
    private static partial Regex VersionRegex();
}

/// <summary>
/// The newest vLLM pip can install, from PyPI. Not GitHub's "latest release": that can be a release
/// candidate that was never published to PyPI (v0.33.0rc3 was, while PyPI's newest was 0.33.0).
/// </summary>
public static class VllmReleases
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The newest version's tag (<c>v0.33.0</c>), or null if PyPI can't be reached.</summary>
    public static async Task<string?> GetLatestTagAsync(CancellationToken ct = default)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync("https://pypi.org/pypi/vllm/json", ct);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return ParseLatestTag(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static string? ParseLatestTag(System.Text.Json.JsonElement pypiDocument) =>
        pypiDocument.TryGetProperty("info", out var info) && info.TryGetProperty("version", out var version)
            && version.GetString() is { } text && VllmLayout.IsVersion(text)
            ? "v" + VllmLayout.NormalizeVersion(text)
            : null;
}
