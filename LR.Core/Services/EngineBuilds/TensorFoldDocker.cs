using System.Globalization;
using System.Text.RegularExpressions;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// TensorFold's XPU engine (Intel GPUs over Level Zero) on Windows, in a Docker Desktop image. The engine
/// opens Level Zero by its Linux name only, so it can't run natively on Windows; through WSL's GPU
/// passthrough (<c>/dev/dxg</c>) it serves Qwen3.8-27B on an Arc Pro B70 as it does on Linux — given
/// <see cref="DriverEnvironment"/>, without which loading fails with <c>ZE_RESULT_ERROR_UNSUPPORTED_SIZE</c>
/// (the engine allocates without Level Zero's relaxed-limits descriptor, and WDDM's maximum allocation is lower).
/// <para>
/// The image builds TensorFold from source at a chosen ref with everything the build pins (Zig, xgrammar,
/// oneDNN's nGEN headers) over a pinned Intel compute runtime, checked by SHA-256. The install folder holds the
/// build context; the image lives in Docker. Updating builds the image again at the newest release.
/// </para>
/// </summary>
public static partial class TensorFoldDocker
{
    /// <summary>The image installs build. Fixed, so a server keeps working across an update of the install it's bound to.</summary>
    public const string ImageTag = "llm-router/tensorfold-xpu:managed";

    /// <summary>The Intel GPU the kernels are compiled for (ocloc's device name): Battlemage, Arc B-series and Arc Pro B60/B70.</summary>
    public const string DefaultDevice = "bmg";

    /// <summary>Tells a Docker install from an unpacked one by name alone, since the router can't look inside a folder on an agent.</summary>
    private const string FolderSuffix = "-xpu-docker";

    /// <summary>Where a preset's drafter folder on the host appears inside the container.</summary>
    public const string ContainerDrafterPath = "/drafter";

    /// <summary>The executable inside the image.</summary>
    public const string ContainerExecutable = "/opt/tensorfold/bin/tensorfold-native";

    /// <summary>
    /// What Intel's driver needs to load a model through WSL: allocations above WDDM's per-allocation limit.
    /// Baked into the image; a preset's environment can override them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DriverEnvironment = new Dictionary<string, string>
    {
        ["NEOReadDebugKeys"] = "1",
        ["AllowUnrestrictedSize"] = "1",
    };

    /// <summary>The Intel runtime the image runs on, as tested on an Arc Pro B70 (driver 32.0.101.8976).</summary>
    public static readonly IReadOnlyList<(string Url, string Sha256)> IntelRuntime =
    [
        ("https://github.com/intel/compute-runtime/releases/download/26.40.40196.8/libze-intel-gpu1_26.40.40196.8-0_amd64.deb",
            "d9c86b2bb195baea6936c2708846cd1fceeadc39bc5e167f728858a5ca6fb86e"),
        ("https://github.com/intel/compute-runtime/releases/download/26.40.40196.8/intel-ocloc_26.40.40196.8-0_amd64.deb",
            "b2868d6d7958cc5ca28fa9b48a7d39961760ee39ef07673d5b58894dd53d6b4e"),
        ("https://github.com/intel/compute-runtime/releases/download/26.40.40196.8/libigdgmm12_22.10.2_amd64.deb",
            "053fceadb84a1d2f9a30a0d1bc9b16424b3f1a90cc49900fcd80aadcf25c8a4d"),
        ("https://github.com/intel/intel-graphics-compiler/releases/download/v2.42.6/intel-igc-core-2_2.42.6+23070_amd64.deb",
            "e20539632f2d99875d8f8b225d5da58b31f98baa54bb47c6328aa98ec473ab9d"),
        ("https://github.com/intel/intel-graphics-compiler/releases/download/v2.42.6/intel-igc-opencl-2_2.42.6+23070_amd64.deb",
            "7f5593cf828503ffb8450967768bde9209e22753bc576296e846b5a751011fa3"),
        ("https://github.com/oneapi-src/level-zero/releases/download/v1.35.0/libze1_1.35.0+u24.04_amd64.deb",
            "8420f4fd710a44bed054afad2f7e0fbc408d8b3e266a2b5464bd4ab71a0dfcf3"),
    ];

    public static string FolderName(string tag) => $"tensorfold-{tag}{FolderSuffix}";

    public static bool IsDockerFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && folder.TrimEnd('/', '\\').Split('/', '\\')[^1].EndsWith(FolderSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the build context of an install is in <paramref name="folder"/> on this machine.</summary>
    public static bool IsInstall(string folder) => IsDockerFolder(folder) && File.Exists(Path.Combine(folder, "Dockerfile"));

    /// <summary>Whether <paramref name="gitRef"/> can be handed to <c>git clone --branch</c> in the image (a tag or branch name).</summary>
    public static bool IsValidRef(string gitRef) => RefPattern().IsMatch(gitRef);

    /// <summary>One container per server, named after its port so a leftover can be found again after a crash.</summary>
    public static string ContainerName(int port) => "llm-router-tensorfold-" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>docker</c> arguments that run a server in the foreground, so its output reaches the wrapper and its
    /// exit means the server's. Variables are passed by name, with their values in <c>docker</c>'s own environment.
    /// </summary>
    /// <param name="port">Published on the host, and what the server inside listens on.</param>
    /// <param name="listenAddress">The host address to publish on.</param>
    /// <param name="modelFolder">A host folder to mount at <see cref="DockerDesktop.ContainerModelPath"/>, or null.</param>
    /// <param name="environmentNames">Variables to pass into the container.</param>
    /// <param name="entrypoint">Null to run the image's <c>tensorfold-native</c>, or another program (the pull script's shell).</param>
    /// <param name="args">The arguments after the entrypoint.</param>
    /// <param name="drafterFolder">A host folder to mount at <see cref="ContainerDrafterPath"/> (a drafter from the library), or null.</param>
    public static List<string> RunArgs(int port, string listenAddress, string? modelFolder, IEnumerable<string> environmentNames, string? entrypoint, IEnumerable<string> args, string? drafterFolder = null)
    {
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var run = new List<string>(DockerDesktop.Host)
        {
            "run", "--rm", "--name", ContainerName(port),
            "--publish", $"{listenAddress}:{portText}:{portText}",
            "--volume", $"{DockerDesktop.HuggingFaceVolume}:/hf-cache",
        };
        run.AddRange(DockerDesktop.GpuArgs);
        if (modelFolder is not null)
            run.AddRange(["--volume", $"{modelFolder}:{DockerDesktop.ContainerModelPath}:ro"]);
        if (drafterFolder is not null)
            run.AddRange(["--volume", $"{drafterFolder}:{ContainerDrafterPath}:ro"]);
        foreach (var name in environmentNames)
            run.AddRange(["--env", name]);
        if (entrypoint is not null)
            run.AddRange(["--entrypoint", entrypoint]);
        run.Add(ImageTag);
        run.AddRange(args);
        return run;
    }

    /// <summary>Removes a server's container. Killing the <c>docker</c> client doesn't: the container would keep the GPU and the port.</summary>
    public static List<string> StopArgs(int port) => [.. DockerDesktop.Host, "rm", "--force", ContainerName(port)];

    /// <summary>The result of an install: where it is, what it's at and how big the image is.</summary>
    public sealed record InstallResult(string InstallPath, string VersionTag, long? SizeBytes);

    /// <summary>
    /// Writes the build context, builds the image at <paramref name="gitRef"/> and checks it computes correctly on
    /// the GPU. Doing it again is how an install is updated.
    /// </summary>
    public static async Task<InstallResult> InstallAsync(string outputDir, string gitRef, string device, BuildProgressSink sink, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("TensorFold's XPU Docker image is for Windows with Docker Desktop. On Linux, compile the XPU recipe instead.");
        if (!IsValidRef(gitRef))
            throw new InvalidOperationException($"'{gitRef}' isn't a tag or branch name.");
        if (!DevicePattern().IsMatch(device))
            throw new InvalidOperationException($"'{device}' isn't an ocloc device name (bmg, dg2, …).");

        await sink.PhaseAsync("docker", "Checking Docker Desktop…");
        await DockerDesktop.EnsureUsableAsync("TensorFold", ct);

        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "Dockerfile"), Dockerfile, ct);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "intel-runtime.sha256"), RuntimeChecksums, ct);

        await sink.PhaseAsync("docker", $"Building the TensorFold XPU image at {gitRef} for {device}. The first build downloads about 1 GB and compiles for a few minutes…");
        var (build, buildOutput) = await DockerDesktop.RunAsync("TensorFold",
            [.. DockerDesktop.Host, "build", "--progress", "plain", "--tag", ImageTag, "--build-arg", $"TF_REF={gitRef}", "--build-arg", $"XPU_DEVICE={device}", outputDir],
            line => sink.LineAsync("docker", line), ct);
        if (build.ExitCode != 0)
            throw new InvalidOperationException($"Building the TensorFold image failed (exit code {build.ExitCode}): {DockerDesktop.Summary(buildOutput)}");

        await sink.PhaseAsync("docker", "Checking that the image computes correctly on the Intel GPU…");
        var (probe, probeOutput) = await DockerDesktop.RunAsync("TensorFold", ProbeArgs(), line => sink.LineAsync("docker", line), ct);
        if (probe.ExitCode != 0 || !probeOutput.Any(l => l.Trim() == "ok"))
            throw new InvalidOperationException(
                $"The TensorFold image couldn't compute on an Intel GPU (exit code {probe.ExitCode}): {DockerDesktop.Summary(probeOutput)}. " +
                "Update the Windows Intel graphics driver, and check that Docker Desktop uses the WSL 2 engine.");

        var (version, versionOutput) = await DockerDesktop.RunAsync("TensorFold", [.. DockerDesktop.Host, "run", "--rm", ImageTag, "--version"], null, ct);
        var reported = versionOutput.Select(l => VersionPattern().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        var tag = reported is not null ? "v" + reported : gitRef;

        return new InstallResult(outputDir, tag, await DockerDesktop.ImageSizeAsync("TensorFold", ImageTag, ct));
    }

    /// <summary>Removes the build context, and the image if nothing is using it. Downloaded models stay in their volume.</summary>
    public static async Task DeleteAsync(string installPath, CancellationToken ct = default)
    {
        if (Directory.Exists(installPath))
            Directory.Delete(installPath, recursive: true);
        try
        {
            await DockerDesktop.RunAsync("TensorFold", [.. DockerDesktop.Host, "image", "rm", ImageTag], null, ct);
        }
        catch (InvalidOperationException)
        {
            // Docker isn't there to ask: nothing more to remove.
        }
    }

    /// <summary>TensorFold's own model-free device test: group means on the GPU against FP64.</summary>
    private static List<string> ProbeArgs() =>
        [.. DockerDesktop.Host, "run", "--rm", "--entrypoint", "/opt/tensorfold/probe/tf-xpu-qwen27-means-test", .. DockerDesktop.GpuArgs, ImageTag];

    private static string RuntimeChecksums =>
        string.Concat(IntelRuntime.Select(r => $"{r.Sha256}  {Uri.UnescapeDataString(r.Url[(r.Url.LastIndexOf('/') + 1)..])}\n"));

    /// <summary>
    /// The image: a build stage that compiles the XPU server, its device test and the grammar library, and a slim
    /// stage that runs them. Everything fetched is pinned — the Intel runtime by the checksums written beside this
    /// file, Zig by the SHA-256 ziglang.org publishes for the version the checkout names.
    /// </summary>
    public static string Dockerfile => $$"""
        # Written by LLM Router. Rebuilt on update; edits are overwritten.
        FROM ubuntu:24.04 AS build
        RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl git g++ xz-utils python3 \
            && rm -rf /var/lib/apt/lists/*

        # Intel's compute runtime and offline compiler, unpacked rather than installed (no repository to trust).
        WORKDIR /build/intel
        COPY intel-runtime.sha256 .
        RUN set -eu; \
            {{string.Join(" \\\n    ", IntelRuntime.Select(r => $"curl -fsSL -O \"{r.Url}\";"))}} \
            sha256sum -c intel-runtime.sha256; \
            for f in *.deb; do dpkg-deb -x "$f" /opt/intel; done
        ENV LD_LIBRARY_PATH=/opt/intel/usr/lib/x86_64-linux-gnu:/opt/intel/usr/local/lib

        ARG TF_REF=main
        ARG XPU_DEVICE={{DefaultDevice}}
        WORKDIR /build
        RUN git clone --depth 1 --branch "$TF_REF" {{TensorFoldSourceBuild.RepoUrl}} src \
            && git -C src describe --tags --always > /build/VERSION

        # The Zig the checkout pins, checked against ziglang.org's published SHA-256.
        RUN set -eu; V="$(cat src/.zig-version)"; \
            curl -fsSL https://ziglang.org/download/index.json -o index.json; \
            set -- $(python3 -c "import json,sys; b=json.load(open('index.json'))[sys.argv[1]]['x86_64-linux']; print(b['tarball'], b['shasum'])" "$V"); \
            curl -fsSL -o zig.tar.xz "$1"; echo "$2  zig.tar.xz" | sha256sum -c; \
            mkdir /opt/zig && tar -xJf zig.tar.xz -C /opt/zig --strip-components=1

        # oneDNN's nGEN headers (the prompt kernels are generated with them) and the xgrammar tag the grammar library pins.
        RUN git clone -q --depth 1 --filter=blob:none --sparse {{TensorFoldSourceBuild.OneDnnRepoUrl}} onednn \
            && git -C onednn sparse-checkout set third_party/ngen \
            && XG="$(grep -o 'pinned = "v[^"]*"' src/zig/src/platform/grammar_library.zig | cut -d'"' -f2)" \
            && git clone -q --depth 1 --branch "${XG:-{{TensorFoldSourceBuild.DefaultXgrammarTag}}}" --recurse-submodules --shallow-submodules {{TensorFoldSourceBuild.XgrammarRepoUrl}} xgrammar

        WORKDIR /build/src
        RUN set -eu; OCLOC="$(ls /opt/intel/usr/bin/ocloc-*)"; \
            ARGS="-Docloc=$OCLOC -Dxpu-device=$XPU_DEVICE -Dngen-include=/build/onednn/third_party/ngen --prefix /out"; \
            /opt/zig/zig build xpu-native $ARGS; \
            /opt/zig/zig build xpu-qwen27-tests $ARGS || true; \
            test -x /out/bin/tf-xpu-qwen27-means-test; \
            /opt/zig/zig build grammar -Dxgrammar=/build/xgrammar --prefix /out

        FROM ubuntu:24.04
        RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
        COPY --from=build /opt/intel/usr/lib /opt/intel/usr/lib
        COPY --from=build /opt/intel/usr/local/lib /opt/intel/usr/local/lib
        COPY --from=build /out/xpu/bin/ /opt/tensorfold/bin/
        COPY --from=build /out/native/lib/ /opt/tensorfold/lib/
        COPY --from=build /out/bin/tf-xpu-qwen27-means-test /opt/tensorfold/probe/
        COPY --from=build /build/VERSION /build/src/LICENSE /opt/tensorfold/

        ENV LD_LIBRARY_PATH=/opt/intel/usr/lib/x86_64-linux-gnu:/opt/intel/usr/local/lib:/usr/lib/wsl/lib \
            {{string.Join(" \\\n    ", DriverEnvironment.Select(e => $"{e.Key}={e.Value}"))}} \
            HF_HOME=/hf-cache
        WORKDIR /opt/tensorfold
        ENTRYPOINT ["{{ContainerExecutable}}"]

        """.ReplaceLineEndings("\n");

    [GeneratedRegex(@"^[A-Za-z0-9][\w.\-/]*$")]
    private static partial Regex RefPattern();

    [GeneratedRegex(@"^[a-z0-9_\-]+$")]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"tensorfold-native\s+(\d+\.\d+\.\d+)")]
    private static partial Regex VersionPattern();
}
