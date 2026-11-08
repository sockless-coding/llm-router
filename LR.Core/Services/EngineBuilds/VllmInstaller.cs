using System.ComponentModel;
using System.Globalization;

using LR.Core.Models;
using LR.Core.Wrapper;

namespace LR.Core.Services.EngineBuilds;

/// <summary>What to install, and where.</summary>
/// <param name="Backend">The GPU backend the wheels should target.</param>
/// <param name="Version">vLLM version without the <c>v</c>; null takes the newest.</param>
/// <param name="OutputDir">
/// The install folder. On Linux, used as is. On Windows only its last segment counts for a new install
/// (the install goes under <see cref="VllmLayout.WslInstallRoot"/> in the distro's home); an existing
/// distro path (an update) is used as it is.
/// </param>
/// <param name="WslDistro">The distro on Windows; null for the machine's default one.</param>
public sealed record VllmInstallRequest(BackendType Backend, string? Version, string OutputDir, string? WslDistro = null);

/// <summary>A finished install.</summary>
public sealed record VllmInstallResult(string InstallPath, string VersionTag, long? SizeBytes, string TargetOs, string TargetArch);

/// <summary>A resolved WSL distro, and whether it can run vLLM well.</summary>
public sealed record WslEnvironment(string Distro, string Home, string? NetworkingMode, string? Gpu);

/// <summary>
/// Installs vLLM into a self-contained folder — a <c>uv</c> binary, a managed Python and a venv — and
/// removes it again. The same code runs on the router and on agents. All the real work is one POSIX
/// shell script, run directly on Linux and through <c>wsl.exe</c> on Windows, so both take the same steps.
/// </summary>
public static class VllmInstaller
{
    private const string WslNotReady =
        "vLLM doesn't run natively on Windows, so it's installed in WSL, and WSL isn't usable from this process: {0} " +
        "Install it with 'wsl --install -d Ubuntu'. WSL distros belong to a Windows user, so LLM Router (or the agent) must also " +
        "run under the account that owns the distro — not as a service under LocalSystem.";

    /// <summary>Finds the WSL distro and its home folder. Throws a message for the user when WSL can't be used.</summary>
    public static async Task<WslEnvironment> ProbeWslAsync(string? distro, CancellationToken ct)
    {
        var lines = new List<string>();
        ProcessRunner.Result result;
        try
        {
            result = await ProcessRunner.RunAsync("wsl.exe",
                [.. VllmLayout.WslDistroArgs(distro), "--cd", "/", "--exec", "sh", "-c", ProbeScript],
                Path.GetTempPath(), null,
                line => { lines.Add(CleanWslLine(line)); return Task.CompletedTask; }, ct);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, WslNotReady, $"wsl.exe couldn't be started ({ex.Message})."));
        }

        if (result.Cancelled) throw new OperationCanceledException(ct);

        string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..].Trim() is { Length: > 0 } v ? v : null;
        if (result.ExitCode != 0 || Field("LR_DISTRO") is not { } name || Field("LR_HOME") is not { } home || !home.StartsWith('/'))
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, WslNotReady,
                string.Join(' ', lines.Where(l => l.Length > 0).Take(3)) is { Length: > 0 } output ? output : $"wsl.exe exited with code {result.ExitCode}."));

        return new WslEnvironment(name, home, Field("LR_NET"), Field("LR_GPU"));
    }

    /// <summary>
    /// The folder a new install ends up in. On Windows that takes asking WSL for the distro and its home
    /// folder, which is why a failure to use WSL at all shows up here, before anything is queued.
    /// </summary>
    public static async Task<string> ResolveInstallPathAsync(VllmInstallRequest request, CancellationToken ct)
    {
        EnsureSupportedOs();
        if (!OperatingSystem.IsWindows() || request.Backend == BackendType.Sycl)
            return request.OutputDir;
        if (VllmLayout.ParseWslPath(request.OutputDir) is { } existing)
            return existing.ToUncPath();

        var wsl = await ProbeWslAsync(request.WslDistro, ct);
        return new WslLocation(wsl.Distro, $"{wsl.Home}/{VllmLayout.WslInstallRoot}/{LastSegment(request.OutputDir)}").ToUncPath();
    }

    /// <summary>Runs the install (or an update of one already in place), reporting through <paramref name="sink"/>.</summary>
    public static async Task<VllmInstallResult> InstallAsync(VllmInstallRequest request, BuildProgressSink sink, CancellationToken ct)
    {
        EnsureSupportedOs();
        if (request.Backend == BackendType.Sycl)
            return await VllmDocker.InstallAsync(request, sink, ct);

        var installPath = await ResolveInstallPathAsync(request, ct);
        var wsl = OperatingSystem.IsWindows() ? VllmLayout.ParseWslPath(installPath) : null;

        if (OperatingSystem.IsWindows())
        {
            var environment = await ProbeWslAsync(wsl!.Distro, ct);
            await sink.PhaseAsync("wsl", $"Installing in WSL distro '{environment.Distro}' at {wsl.LinuxPath}.");
            if (environment.Gpu is { } gpu)
                await sink.LineAsync("wsl", $"GPU seen from WSL: {gpu}");
            else if (request.Backend == BackendType.Cuda)
                await sink.LineAsync("wsl", "No GPU seen from WSL (nvidia-smi found nothing). Update the Windows NVIDIA driver if vLLM can't find the GPU; nothing needs installing inside the distro.");
        }

        var dest = wsl?.LinuxPath ?? request.OutputDir;
        var script = BuildScript(dest, request.Backend, request.Version);

        string? version = null, arch = null;
        long? size = null;
        var lastLines = new Queue<string>();
        var (executable, args, workingDirectory) = ScriptCommand(script, wsl?.Distro);

        await sink.PhaseAsync("install", $"Installing vLLM {request.Version ?? "(newest)"} for {request.Backend}. This downloads several GB and takes about 20 GB on disk…");
        var result = await RunAsync(executable, args, workingDirectory, async raw =>
        {
            var line = CleanWslLine(raw);
            if (line.StartsWith("LR_VLLM_VERSION=", StringComparison.Ordinal)) { version = line["LR_VLLM_VERSION=".Length..].Trim(); return; }
            if (line.StartsWith("LR_VLLM_ARCH=", StringComparison.Ordinal)) { arch = line["LR_VLLM_ARCH=".Length..].Trim(); return; }
            if (line.StartsWith("LR_VLLM_SIZE=", StringComparison.Ordinal))
            {
                if (long.TryParse(line["LR_VLLM_SIZE=".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) size = s;
                return;
            }
            if (line.StartsWith("LR_PHASE=", StringComparison.Ordinal)) { await sink.PhaseAsync("install", line["LR_PHASE=".Length..]); return; }
            if (string.IsNullOrWhiteSpace(line)) return;
            lastLines.Enqueue(line);
            if (lastLines.Count > 6) lastLines.Dequeue();
            await sink.LineAsync("install", line);
        }, ct);

        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0 || version is null)
            throw new InvalidOperationException($"Installing vLLM failed (exit code {result.ExitCode}): {string.Join(" | ", lastLines)}");

        if (wsl is not null && !string.Equals((await ProbeWslAsync(wsl.Distro, ct)).NetworkingMode, "mirrored", StringComparison.OrdinalIgnoreCase))
            await sink.LineAsync("wsl",
                "Note: this distro uses NAT networking, so its servers are only reachable from this PC. For a server on a remote agent, " +
                "set networkingMode=mirrored in the agent user's .wslconfig and allow the port through the Hyper-V firewall.");

        return new VllmInstallResult(installPath, "v" + version, size, "ubuntu", arch == "aarch64" ? "arm64" : "x64");
    }

    /// <summary>
    /// Deletes an install on this machine. A folder in WSL is removed from inside the distro: over the
    /// <c>\\wsl.localhost</c> share, <see cref="Directory.Delete(string, bool)"/> fails on a venv's
    /// absolute symlinks. Only ever touches the folder named by a distro path with a real parent.
    /// </summary>
    public static async Task DeleteAsync(string installPath, CancellationToken ct = default)
    {
        if (VllmDocker.IsDockerFolder(installPath))
        {
            await VllmDocker.DeleteAsync(installPath, ct);
            return;
        }

        if (VllmLayout.ParseWslPath(installPath) is not { } wsl)
        {
            if (Directory.Exists(installPath)) Directory.Delete(installPath, recursive: true);
            return;
        }

        if (wsl.LinuxPath.Count(c => c == '/') < 2)
            throw new InvalidOperationException($"Refusing to delete '{wsl.LinuxPath}' in WSL: it's too close to the filesystem root.");

        var result = await ProcessRunner.RunAsync("wsl.exe", ["-d", wsl.Distro, "--cd", "/", "--exec", "rm", "-rf", "--", wsl.LinuxPath],
            Path.GetTempPath(), null, _ => Task.CompletedTask, ct);
        if (!result.Cancelled && result.ExitCode != 0)
            throw new InvalidOperationException($"Could not delete {wsl.LinuxPath} in WSL distro '{wsl.Distro}' (exit code {result.ExitCode}).");
    }

    private static void EnsureSupportedOs()
    {
        if (OperatingSystem.IsMacOS())
            throw new InvalidOperationException("vLLM on macOS goes through the separate vLLM-Metal plugin, which LLM Router doesn't install.");
    }

    private static string LastSegment(string path) => path.TrimEnd('/', '\\').Split('/', '\\')[^1];

    /// <summary>wsl.exe's own messages are UTF-16; read as UTF-8 they come through with NULs between the letters.</summary>
    private static string CleanWslLine(string line) => line.Replace("\0", "").TrimEnd('\r');

    private static (string Executable, IReadOnlyList<string> Args, string WorkingDirectory) ScriptCommand(string script, string? distro) => distro is not null
        ? ("wsl.exe", ["-d", distro, "--cd", "/", "--exec", "sh", "-c", script], Path.GetTempPath())
        : ("/bin/sh", ["-c", script], Path.GetTempPath());

    private static async Task<ProcessRunner.Result> RunAsync(string executable, IReadOnlyList<string> args, string workingDirectory, Func<string, Task> onLine, CancellationToken ct)
    {
        try
        {
            return await ProcessRunner.RunAsync(executable, args, workingDirectory, null, onLine, ct);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Couldn't start {executable}: {ex.Message}");
        }
    }

    private const string ProbeScript =
        """
        echo "LR_DISTRO=$WSL_DISTRO_NAME"
        echo "LR_HOME=$HOME"
        echo "LR_NET=$(wslinfo --networking-mode 2>/dev/null)"
        echo "LR_GPU=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1)"
        """;

    /// <summary>
    /// The install script. Everything lands under <paramref name="dest"/> so removing the folder removes
    /// the install; only Hugging Face's model cache lives outside it. <c>uv</c> and its checksum come from
    /// its GitHub release, fetched with Python's standard library because a fresh distro may have no curl
    /// (Ubuntu's WSL image has python3 but neither curl nor python3-venv). Commands are vLLM's documented
    /// ones (docs: getting_started/installation/gpu); <c>--torch-backend=auto</c> picks PyTorch for the
    /// installed driver. <c>examples/</c> from the matching tag lets recipes that name
    /// <c>examples/tool_chat_template_*.jinja</c> run, since servers start in this folder and wheels don't include it.
    /// </summary>
    public static string BuildScript(string dest, BackendType backend, string? version)
    {
        if (version is not null)
            version = VllmLayout.NormalizeVersion(version);

        var requirement = version is null ? "vllm" : $"vllm=={version}";
        var packages = backend switch
        {
            BackendType.Cuda => $"{WrapperHost.PosixQuote(requirement)} --torch-backend=auto",
            BackendType.Hip => $"{WrapperHost.PosixQuote(requirement)} --extra-index-url https://wheels.vllm.ai/rocm/",
            _ => throw new InvalidOperationException($"vLLM can't be installed in a venv for {backend}; use NVIDIA CUDA or AMD ROCm, or point the server at a venv you set up yourself."),
        };

        return $$"""
            set -eu
            DEST={{WrapperHost.PosixQuote(dest)}}
            export HOME="${HOME:-$DEST}"
            export UV_PYTHON_INSTALL_DIR="$DEST/.python"
            export UV_NO_PROGRESS=1
            export PIP_PROGRESS_BAR=off
            export XDG_CACHE_HOME="$DEST/.cache"
            mkdir -p "$DEST"
            cd "$DEST"

            ARCH="$(uname -m)"
            case "$ARCH" in
              x86_64|aarch64) ;;
              *) echo "vLLM has no wheels for $ARCH." >&2; exit 1 ;;
            esac
            echo "LR_VLLM_ARCH=$ARCH"

            if command -v uv >/dev/null 2>&1; then
              UV="$(command -v uv)"
            else
              UV="$DEST/.uv/uv"
              if [ ! -x "$UV" ]; then
                if ! command -v python3 >/dev/null 2>&1; then
                  echo "python3 is needed to fetch uv and isn't installed. On Ubuntu: sudo apt install python3" >&2
                  exit 1
                fi
                echo "LR_PHASE=Downloading uv (the Python package installer vLLM recommends)..."
                python3 -I -c {{WrapperHost.PosixQuote(UvBootstrap)}} "$DEST/.uv" "$ARCH"
              fi
            fi

            echo "LR_PHASE=Creating the Python {{VllmLayout.PythonVersion}} environment..."
            "$UV" venv --python {{VllmLayout.PythonVersion}} --seed --managed-python --allow-existing "$DEST/.venv"

            echo "LR_PHASE=Installing vLLM and PyTorch (several GB)..."
            "$UV" pip install --python "$DEST/.venv/bin/python" --upgrade {{packages}}

            if "$DEST/.venv/bin/vllm" download-kernels --help >/dev/null 2>&1; then
              echo "LR_PHASE=Downloading vLLM's GPU kernels..."
              "$DEST/.venv/bin/vllm" download-kernels
            fi

            VERSION="$("$DEST/.venv/bin/python" -I -c 'import importlib.metadata as m; print(m.version("vllm").split("+")[0])')"

            echo "LR_PHASE=Fetching vLLM's example chat templates..."
            "$DEST/.venv/bin/python" -I -c {{WrapperHost.PosixQuote(ExamplesFetcher)}} "$VERSION" "$DEST/examples" \
              || echo "Could not fetch vLLM's examples folder; recipes that name a template under examples/ won't start until it's there."

            rm -rf "$DEST/.cache"
            echo "LR_VLLM_SIZE=$(du -sb "$DEST" | cut -f1)"
            echo "LR_VLLM_VERSION=$VERSION"
            """.ReplaceLineEndings("\n");
    }

    /// <summary>Downloads uv's release archive to <c>argv[1]</c>, checked against the checksum published next to it.</summary>
    private const string UvBootstrap =
        """
        import hashlib, io, os, sys, tarfile, urllib.request
        dest, arch = sys.argv[1], sys.argv[2]
        name = "uv-%s-unknown-linux-gnu.tar.gz" % arch
        url = "https://github.com/astral-sh/uv/releases/latest/download/" + name
        data = urllib.request.urlopen(url, timeout=120).read()
        expected = urllib.request.urlopen(url + ".sha256", timeout=60).read().decode().split()[0]
        actual = hashlib.sha256(data).hexdigest()
        if actual != expected:
            sys.exit("uv's checksum doesn't match (%s, expected %s); not installing it." % (actual, expected))
        os.makedirs(dest, exist_ok=True)
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
            for member in archive.getmembers():
                base = os.path.basename(member.name)
                if member.isfile() and base in ("uv", "uvx"):
                    target = os.path.join(dest, base)
                    with archive.extractfile(member) as source, open(target, "wb") as out:
                        out.write(source.read())
                    os.chmod(target, 0o755)
        """;

    /// <summary>Unpacks only regular files under <c>examples/</c> from the vLLM source archive of tag <c>argv[1]</c> into <c>argv[2]</c>.</summary>
    private const string ExamplesFetcher =
        """
        import os, sys, tarfile, urllib.request
        version, dest = sys.argv[1], sys.argv[2]
        url = "https://github.com/vllm-project/vllm/archive/refs/tags/v%s.tar.gz" % version
        count = 0
        with urllib.request.urlopen(url, timeout=120) as response, tarfile.open(fileobj=response, mode="r|gz") as archive:
            for member in archive:
                parts = member.name.split("/")
                if len(parts) < 3 or parts[1] != "examples" or not member.isfile() or ".." in parts:
                    continue
                if not parts[-1].endswith((".jinja", ".json", ".yaml", ".yml")):
                    continue
                target = os.path.join(dest, *parts[2:])
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with archive.extractfile(member) as source, open(target, "wb") as out:
                    out.write(source.read())
                count += 1
        print("Fetched %d example files." % count)
        """;
}
