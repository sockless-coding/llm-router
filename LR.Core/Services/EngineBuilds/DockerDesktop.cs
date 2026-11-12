using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// What the engines that run an Intel GPU through Docker Desktop on Windows share (vLLM's and TensorFold's
/// XPU images): the CLI and its context, the device and libraries that reach the GPU through WSL, and
/// running <c>docker</c> with its output collected.
/// </summary>
public static partial class DockerDesktop
{
    public const string Executable = "docker.exe";

    /// <summary>
    /// Addresses Docker Desktop's Linux engine (the only one exposing <c>/dev/dxg</c>) by its named pipe rather than the
    /// <c>desktop-linux</c> context: contexts live in the signed-in user's <c>~\.docker</c>, which a LocalSystem service
    /// doesn't have, while the pipe grants SYSTEM access. It also keeps working when the CLI is left on another context.
    /// </summary>
    public static readonly string[] Host = ["--host", "npipe:////./pipe/dockerDesktopLinuxEngine"];

    /// <summary>What a container needs to reach the GPU through WSL. No <c>--privileged</c>.</summary>
    public static readonly string[] GpuArgs =
    [
        "--device", "/dev/dxg", "--shm-size", "16g",
        "--volume", "/usr/lib/wsl/lib:/usr/lib/wsl/lib:ro",
        "--volume", "/usr/lib/wsl/drivers:/usr/lib/wsl/drivers:ro",
    ];

    /// <summary>Hugging Face's cache, kept across containers (and shared by the engines) so a model is only downloaded once.</summary>
    public const string HuggingFaceVolume = "llm-router-hf-cache";

    /// <summary>Where a preset's local model folder appears inside a container.</summary>
    public const string ContainerModelPath = "/model";

    /// <summary>
    /// The host folder to mount for a model, or null for a Hugging Face id, which the container downloads.
    /// Throws for a network path, which Docker Desktop can't mount.
    /// </summary>
    public static string? ModelFolder(string model)
    {
        var trimmed = model.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException($"Docker Desktop can't mount the model folder '{trimmed}'. Use a folder on a local drive, like D:\\models\\qwen, or a Hugging Face id.");
        if (!WindowsDriveRegex().IsMatch(trimmed))
            return null;
        var withoutSlash = trimmed.TrimEnd('/', '\\');
        return withoutSlash.Length == 2 ? trimmed : withoutSlash;
    }

    /// <summary>The error for a Docker Desktop that can't be used, completed with what went wrong.</summary>
    public static string Unusable(string engine, string detail) =>
        $"{engine} on an Intel GPU runs in Docker Desktop, which isn't usable from this process: {detail} Install Docker Desktop, select its Linux/WSL 2 engine and " +
        "wait until it reports that it's running. Docker Desktop only runs while a user is signed in, so keep it started for that user even when LLM Router runs as a service.";

    /// <summary>Fails with <see cref="Unusable"/> when Docker Desktop's Linux engine doesn't answer.</summary>
    public static async Task EnsureUsableAsync(string engine, CancellationToken ct)
    {
        var (check, output) = await RunAsync(engine, [.. Host, "info", "--format", "{{.ServerVersion}}"], null, ct);
        if (check.ExitCode != 0)
            throw new InvalidOperationException(Unusable(engine, Summary(output)));
    }

    /// <summary>An image's size in bytes, or null when Docker can't say.</summary>
    public static async Task<long?> ImageSizeAsync(string engine, string imageTag, CancellationToken ct)
    {
        var (result, output) = await RunAsync(engine, [.. Host, "image", "inspect", "--format", "{{.Size}}", imageTag], null, ct);
        return result.ExitCode == 0 && long.TryParse(output.LastOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null;
    }

    /// <summary>Runs <c>docker</c>, collecting its non-empty output lines (and passing each to <paramref name="onLine"/>).</summary>
    public static async Task<(ProcessRunner.Result Result, List<string> Output)> RunAsync(string engine, IReadOnlyList<string> args, Func<string, Task>? onLine, CancellationToken ct)
    {
        var output = new List<string>();
        ProcessRunner.Result result;
        try
        {
            result = await ProcessRunner.RunAsync(Executable, args, Path.GetTempPath(), null, async line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                output.Add(line);
                if (onLine is not null) await onLine(line);
            }, ct);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(Unusable(engine, $"{Executable} couldn't be started ({ex.Message})."));
        }

        if (result.Cancelled) throw new OperationCanceledException(ct);
        return (result, output);
    }

    /// <summary>The last few lines of a command's output, for an error message.</summary>
    public static string Summary(List<string> output) => output.Count == 0 ? "it reported nothing." : string.Join(" | ", output.TakeLast(6));

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDriveRegex();
}
