using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// vLLM on an Intel GPU, run from a Docker image on Windows. A venv in WSL never served a completion on an
/// Arc Pro B70, while vLLM's own XPU image does with the fixes here, so this backend (<see cref="BackendType.Sycl"/>)
/// installs by building that image. The install folder only holds the build context; the image lives in
/// Docker. The image is pinned because the fixes patch vLLM's source: a different version needs them
/// checked again, so changing it is a code change here rather than a choice on the install form.
/// <para>
/// Everything that makes the image work is in the <see cref="Dockerfile"/> and <see cref="PatchScript"/>.
/// A host with an NVIDIA card too is what most of it is about: Docker Desktop shares
/// <c>/usr/lib/wsl/lib</c>, which holds NVIDIA's libraries, so vLLM and Triton both see CUDA next to XPU.
/// </para>
/// </summary>
public static partial class VllmDocker
{
    /// <summary>The vLLM in <see cref="BaseImage"/>; the version every install of this backend has.</summary>
    public const string PinnedVersion = "0.27.2rc1.dev77";

    public const string BaseImage = "vllm/vllm-openai-xpu@sha256:f01e24f6c7ff01f1e0662234255a1372297d1dbd89d003cf13c8fad3eab1ba4f";

    /// <summary>The image installs build. Fixed, so a server keeps working across an update of the install it's bound to.</summary>
    public const string ImageTag = "llm-router/vllm-xpu:managed";

    public const string Executable = "docker.exe";

    /// <summary>Hugging Face's cache, kept across containers so a model is only downloaded once.</summary>
    public const string HuggingFaceVolume = "llm-router-hf-cache";

    /// <summary>Where a preset's local model folder appears inside the container.</summary>
    public const string ContainerModelPath = "/model";

    /// <summary>Tells a Docker install from a venv by name alone, since the router can't look inside a folder on an agent.</summary>
    private const string FolderSuffix = "-xpu-docker";

    /// <summary>Only Docker Desktop's Linux engine exposes <c>/dev/dxg</c>, and a fresh install can leave the CLI on another context.</summary>
    private static readonly string[] Context = ["--context", "desktop-linux"];

    private const string DockerUnusable =
        "vLLM on an Intel GPU runs in Docker Desktop, which isn't usable from this process: {0} Install Docker Desktop, select its Linux/WSL 2 engine and " +
        "wait until it reports that it's running. The account running LLM Router (or the agent) must be able to use it, so not a service under LocalSystem.";

    public static string FolderName => $"vllm-v{PinnedVersion}{FolderSuffix}";

    public static bool IsDockerFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && folder.TrimEnd('/', '\\').Split('/', '\\')[^1].EndsWith(FolderSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the build context of an install is in <paramref name="folder"/> on this machine.</summary>
    public static bool IsInstall(string folder) => IsDockerFolder(folder) && File.Exists(Path.Combine(folder, "Dockerfile"));

    /// <summary>The tag to record for an install. Throws when asked for any version but the pinned one.</summary>
    public static string ResolveTag(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version) && VllmLayout.NormalizeVersion(version) != PinnedVersion)
            throw new InvalidOperationException($"vLLM on an Intel GPU uses the tested image, vLLM {PinnedVersion}. Leave the version empty.");
        return "v" + PinnedVersion;
    }

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

    /// <summary>One container per server, named after its port so a leftover can be found again after a crash.</summary>
    public static string ContainerName(int port) => "llm-router-vllm-" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>docker</c> arguments that run a server in the foreground, so its output reaches the wrapper
    /// and its exit means the server's. Variables are passed by name, with their values in
    /// <c>docker</c>'s own environment, so a token never appears on a command line.
    /// </summary>
    /// <param name="port">Published on the host, and what the server inside must listen on.</param>
    /// <param name="listenAddress">The host address to publish on: loopback, unless an agent's server is reached over the network.</param>
    /// <param name="modelFolder">The result of <see cref="ModelFolder"/>.</param>
    /// <param name="environmentNames">Variables to pass into the container.</param>
    /// <param name="serveArgs">Everything after <c>vllm</c>, the image's entrypoint.</param>
    public static List<string> RunArgs(int port, string listenAddress, string? modelFolder, IEnumerable<string> environmentNames, IEnumerable<string> serveArgs)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>(Context)
        {
            "run", "--rm", "--name", ContainerName(port),
            "--publish", $"{listenAddress}:{portText}:{portText}",
            "--volume", $"{HuggingFaceVolume}:/hf-cache",
        };
        args.AddRange(GpuArgs);
        if (modelFolder is not null)
            args.AddRange(["--volume", $"{modelFolder}:{ContainerModelPath}:ro"]);
        foreach (var name in environmentNames)
            args.AddRange(["--env", name]);
        args.Add(ImageTag);
        args.AddRange(serveArgs);
        return args;
    }

    /// <summary>Removes a server's container. Killing the <c>docker</c> client doesn't: the container would keep the GPU and the port.</summary>
    public static List<string> StopArgs(int port) => [.. Context, "rm", "--force", ContainerName(port)];

    /// <summary>
    /// The MTP speculative-decoding patches for Qwen3.8 from the MIT-licensed
    /// github.com/SergiioB/intel-arc-pro-b70-inference-cookbook, unmodified (see <c>VllmXpuPatches/LICENSE</c>), in
    /// the order they apply. Without a speculative config they change nothing, and the draft ones are also
    /// gated on the variables in <see cref="Qwen38Preset"/>, so one image serves every preset.
    /// </summary>
    public static readonly IReadOnlyList<string> CookbookPatches =
    [
        "patch_mtp_nightly.py",
        "patch_mtp_boundary.py",
        "patch_gdn_mixed_split_v5.py",
        "patch_draft_lmhead_int4.py",
        "patch_draft_mtp_int4.py",
    ];

    /// <summary>
    /// The settings proven with Qwen3.8-27B GPTQ INT4 on an Arc Pro B70: MTP with four speculative tokens and a
    /// 100K context. Reasoning defaults to low because that model's own template defaults to xhigh, which
    /// writes far more before answering; a request can still ask for more.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Qwen38Preset = new Dictionary<string, string>
    {
        [VllmPresetSettings.MaxModelLen] = "100000",
        [VllmPresetSettings.GpuMemoryUtilization] = "0.75",
        [VllmPresetSettings.Dtype] = "float16",
        [VllmPresetSettings.ToolCallParser] = "qwen3_coder",
        [VllmPresetSettings.AutoToolChoice] = "true",
        [VllmPresetSettings.ReasoningParser] = "qwen3",
        [VllmPresetSettings.ExtraArgs] = string.Join('\n',
            "--quantization", "gptq",
            "--kv-cache-dtype", "fp8",
            "--kv-cache-memory-bytes", "4617089843",
            "--max-num-seqs", "1",
            "--max-num-batched-tokens", "8192",
            "--enable-prefix-caching",
            "--language-model-only",
            "--generation-config", "auto",
            "--default-chat-template-kwargs", """{"reasoning_effort":"low"}""",
            "--speculative-config", """{"method":"mtp","num_speculative_tokens":4}"""),
        [VllmPresetSettings.Environment] = "B70_MTP_BF16_DRAFT=1\nB70_DRAFT_LMHEAD_INT4=1\nB70_DRAFT_MTP_INT4=1",
    };

    private static string ReadCookbookFile(string file)
    {
        using var stream = typeof(VllmDocker).Assembly.GetManifestResourceStream("VllmXpuPatches." + file)
            ?? throw new InvalidOperationException($"{file} is missing from this build of LLM Router.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>What the container needs to reach the GPU through WSL. No <c>--privileged</c>.</summary>
    private static readonly string[] GpuArgs =
    [
        "--device", "/dev/dxg", "--shm-size", "16g",
        "--volume", "/usr/lib/wsl/lib:/usr/lib/wsl/lib:ro",
        "--volume", "/usr/lib/wsl/drivers:/usr/lib/wsl/drivers:ro",
    ];

    /// <summary>Writes the build context, builds the image and checks it can use the GPU. Doing it again is how an install is updated.</summary>
    public static async Task<VllmInstallResult> InstallAsync(VllmInstallRequest request, BuildProgressSink sink, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("vLLM on an Intel GPU is only set up for Windows with Docker Desktop.");
        ResolveTag(request.Version);

        await sink.PhaseAsync("docker", "Checking Docker Desktop…");
        var (check, checkOutput) = await RunDockerAsync([.. Context, "info", "--format", "{{.ServerVersion}}"], null, ct);
        if (check.ExitCode != 0)
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DockerUnusable, Summary(checkOutput)));

        Directory.CreateDirectory(request.OutputDir);
        await File.WriteAllTextAsync(Path.Combine(request.OutputDir, "Dockerfile"), Dockerfile, ct);
        await File.WriteAllTextAsync(Path.Combine(request.OutputDir, "patch_vllm.py"), PatchScript, ct);
        await File.WriteAllTextAsync(Path.Combine(request.OutputDir, "probe.py"), ProbeScript, ct);
        var cookbook = Path.Combine(request.OutputDir, "cookbook");
        Directory.CreateDirectory(cookbook);
        foreach (var file in CookbookPatches.Append("LICENSE"))
            await File.WriteAllTextAsync(Path.Combine(cookbook, file), ReadCookbookFile(file), ct);

        await sink.PhaseAsync("docker", "Building the vLLM image. The first build downloads about 18 GB…");
        var (build, buildOutput) = await RunDockerAsync([.. Context, "build", "--progress", "plain", "--tag", ImageTag, request.OutputDir],
            line => sink.LineAsync("docker", line), ct);
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"Building the vLLM image failed (exit code {build.ExitCode}): {Summary(buildOutput)}");

        await sink.PhaseAsync("docker", "Checking that the image can use the Intel GPU…");
        var (probe, probeOutput) = await RunDockerAsync(ProbeArgs(), line => sink.LineAsync("docker", line), ct);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException($"The vLLM image couldn't use an Intel GPU (exit code {probe.ExitCode}): {Summary(probeOutput)}");

        var version = probeOutput.LastOrDefault(l => l.StartsWith("LR_VLLM_VERSION=", StringComparison.Ordinal))?["LR_VLLM_VERSION=".Length..].Trim();
        if (string.IsNullOrEmpty(version))
            throw new InvalidOperationException($"The check of the vLLM image didn't report a version: {Summary(probeOutput)}");

        return new VllmInstallResult(request.OutputDir, "v" + version, await ImageSizeAsync(ct), "ubuntu", "x64");
    }

    /// <summary>Removes the build context, and the image if nothing is using it. The downloaded models stay in their volume.</summary>
    public static async Task DeleteAsync(string installPath, CancellationToken ct = default)
    {
        if (Directory.Exists(installPath))
            Directory.Delete(installPath, recursive: true);
        try
        {
            await RunDockerAsync([.. Context, "image", "rm", ImageTag], null, ct);
        }
        catch (InvalidOperationException)
        {
            // Docker isn't there to ask: nothing more to remove.
        }
    }

    private static List<string> ProbeArgs()
    {
        var args = new List<string>(Context) { "run", "--rm", "--entrypoint", "python" };
        args.AddRange(GpuArgs);
        args.AddRange([ImageTag, "/opt/llm-router/probe.py"]);
        return args;
    }

    private static async Task<long?> ImageSizeAsync(CancellationToken ct)
    {
        var (result, output) = await RunDockerAsync([.. Context, "image", "inspect", "--format", "{{.Size}}", ImageTag], null, ct);
        return result.ExitCode == 0 && long.TryParse(output.LastOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null;
    }

    private static async Task<(ProcessRunner.Result Result, List<string> Output)> RunDockerAsync(IReadOnlyList<string> args, Func<string, Task>? onLine, CancellationToken ct)
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
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, DockerUnusable, $"{Executable} couldn't be started ({ex.Message})."));
        }

        if (result.Cancelled) throw new OperationCanceledException(ct);
        return (result, output);
    }

    private static string Summary(List<string> output) => output.Count == 0 ? "it reported nothing." : string.Join(" | ", output.TakeLast(6));

    /// <summary>
    /// The image. The fixes go in at build time, so one that no longer fits fails the install rather than a
    /// server start. Three things are needed on a host that also has an NVIDIA card:
    /// <list type="bullet">
    /// <item>The working directory: the base image's is <c>/workspace/vllm</c>, a source checkout. A <c>python -m</c>
    /// subprocess started there imports that copy of vLLM instead of the patched one in the venv.</item>
    /// <item>The patched platform check, for vLLM's "Only one platform plugin" error.</item>
    /// <item><c>CUDA_VISIBLE_DEVICES</c> and <c>TRITON_DEFAULT_BACKEND</c>, for Triton's "2 active drivers" error. The first
    /// makes vLLM keep Triton on, not just torch.compile get past it.</item>
    /// </list>
    /// The Level Zero and oneCCL settings come from the setup this was proven with. A preset's variables can override any of them.
    /// </summary>
    public static string Dockerfile => $$"""
        FROM {{BaseImage}}

        COPY patch_vllm.py probe.py /opt/llm-router/
        COPY cookbook/ /opt/llm-router/cookbook/
        RUN python /opt/llm-router/patch_vllm.py \
            {{string.Join("\n    ", CookbookPatches.Select(p => $"&& python /opt/llm-router/cookbook/{p} \\"))}}
            && ln -s /workspace/vllm/examples /opt/llm-router/examples

        ENV VLLM_TARGET_DEVICE=xpu \
            CUDA_VISIBLE_DEVICES=-1 \
            TRITON_DEFAULT_BACKEND=intel \
            HF_HOME=/hf-cache \
            LD_LIBRARY_PATH=/usr/lib/wsl/lib:/tmp/ucx_install/lib:/opt/venv/lib:/usr/local/lib \
            ZE_FLAT_DEVICE_HIERARCHY=COMPOSITE \
            ZE_AFFINITY_MASK=0 \
            VLLM_XPU_ENABLE_XPU_GRAPH=1 \
            PYTORCH_ALLOC_CONF=expandable_segments:True \
            CCL_ZE_IPC_EXCHANGE=sockets \
            CCL_ATL_TRANSPORT=ofi \
            CCL_ENABLE_SYCL_KERNELS=0 \
            CCL_TOPO_P2P_ACCESS=0 \
            CCL_TOPO_FABRIC_VERTEX_CONNECTION_CHECK=0 \
            CCL_ZE_CACHE_OPEN_IPC_HANDLES=0 \
            SYCL_UR_USE_LEVEL_ZERO_V2=0 \
            SYCL_PI_LEVEL_ZERO_USE_IMMEDIATE_COMMANDLISTS=0 \
            TORCH_LLM_ALLREDUCE=1

        WORKDIR /opt/llm-router
        ENTRYPOINT ["/opt/venv/bin/vllm"]

        """.ReplaceLineEndings("\n");

    /// <summary>Patches vLLM in the image's venv. Each fix must match exactly once, so a changed vLLM stops the build.</summary>
    public static readonly string PatchScript =
        """
        import re
        from pathlib import Path

        VLLM = Path("/opt/venv/lib/python3.12/site-packages/vllm")


        def patch(relative, pattern, replacement):
            path = VLLM / relative
            source = path.read_text(encoding="utf-8")
            patched, count = re.subn(pattern, replacement, source, flags=re.MULTILINE)
            if count != 1:
                raise SystemExit(f"Expected one match in {path}, found {count}: this vLLM needs its fixes checked again.")
            compile(patched, str(path), "exec")
            path.write_text(patched, encoding="utf-8")
            print(f"Patched {path}")


        # NVML finds any NVIDIA card through the libraries WSL shares, and CUDA then activates next to XPU.
        patch(
            "platforms/__init__.py",
            r"^(def cuda_platform_plugin\(\) -> str \| None:\n)",
            r'\1    if envs.VLLM_TARGET_DEVICE == "xpu":\n        return None\n',
        )

        # oneCCL's IPC path needs a Linux DRM directory that WSL doesn't have, and reducing over one rank does nothing.
        patch(
            "v1/worker/xpu_worker.py",
            r"^([ \t]+)(torch\.distributed\.all_reduce\(torch\.zeros\(1\)\.xpu\(\)\))[ \t]*$",
            r"\1if self.parallel_config.world_size > 1:\n\1    \2",
        )

        """.ReplaceLineEndings("\n");

    /// <summary>Run once after a build: it has to find the GPU, compute on it, and agree with itself about the platform.</summary>
    public static readonly string ProbeScript =
        """
        import importlib.metadata
        import sys

        import torch
        from vllm.platforms import current_platform
        from vllm.triton_utils import HAS_TRITON

        if type(current_platform).__name__ != "XPUPlatform":
            sys.exit(f"vLLM chose {type(current_platform).__name__} rather than XPU.")
        if not torch.xpu.is_available():
            sys.exit("No Intel GPU is visible from the container. Update the Windows Intel graphics driver.")
        if float(torch.ones(16, device="xpu").sum().cpu()) != 16.0:
            sys.exit("A test calculation on the Intel GPU returned the wrong result.")
        if not HAS_TRITON:
            sys.exit("vLLM disabled Triton, so model compilation would fail.")

        print(f"GPU: {torch.xpu.get_device_name(0)}")
        print("LR_VLLM_VERSION=" + importlib.metadata.version("vllm").split("+")[0])

        """.ReplaceLineEndings("\n");

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDriveRegex();
}
