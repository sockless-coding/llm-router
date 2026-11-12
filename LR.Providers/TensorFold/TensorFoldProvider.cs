using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;
using LR.Providers.Vllm;

namespace LR.Providers.TensorFold;

/// <summary>
/// Runs TensorFold's native server (github.com/ashhart/TensorFold), <c>tensorfold-native serve</c>:
/// OpenAI-compatible <c>/v1/chat/completions</c> and Anthropic-compatible <c>/v1/messages</c>, a
/// <c>/health</c>, and a <c>/v1/models</c> carrying the context window it resolved — but no llama.cpp
/// <c>/props</c> or <c>/slots</c>. Much like vLLM:
/// <list type="bullet">
/// <item>Readiness comes from <c>/health</c>, which answers once the model is loaded.</item>
/// <item>Every server runs under one fixed name (<see cref="TensorFoldPresetSettings.ServedModelName"/>)
/// and each request is rewritten to it, since clients ask by preset name or alias.</item>
/// <item><c>serve</c> never downloads, so on Linux and macOS a repo id is launched through a small
/// shell script that pulls it first when it isn't in the Hugging Face cache, then <c>exec</c>s the
/// server. A first start can download a whole checkpoint, so the startup timeout is hours.</item>
/// </list>
/// On Windows (a build from source; there's no Windows release) the server is launched directly, and a
/// repo id has to have been pulled already.
/// </summary>
public class TensorFoldProvider : ManagedServerProviderBase
{
    public override ServerEngine Engine => ServerEngine.TensorFold;

    private readonly ILogger<TensorFoldProvider> _logger;
    private volatile int _maxConcurrent;

    public TensorFoldProvider(ILogger<TensorFoldProvider> logger, IServiceScopeFactory scopeFactory)
        : base(logger, scopeFactory)
    {
        _logger = logger;
    }

    protected override TimeSpan StartupTimeout => TimeSpan.FromHours(4);

    // Long replies from large models (and thinking) can go a while between chunks.
    protected override TimeSpan RequestTimeout => TimeSpan.FromMinutes(30);

    protected override bool HasLlamaServerEndpoints => false;

    protected override bool ReadyWhenHealthy => true;

    public override int? MaxConcurrentRequests
    {
        get
        {
            if (_maxConcurrent == 0)
                _maxConcurrent = ResolveMaxConcurrentFromActivePreset();
            return _maxConcurrent;
        }
    }

    protected override ServerLaunchSpec BuildLaunchSpec(ModelPreset preset)
    {
        if (string.IsNullOrEmpty(InstallFolderPath))
            throw new InvalidOperationException("TensorFold's install folder is not set — bind the server to a TensorFold install from the Engines page.");

        if (TensorFoldDocker.IsDockerFolder(InstallFolderPath))
            return BuildDockerLaunchSpec(preset);

        var executable = TensorFoldLayout.ExecutablePath(InstallFolderPath, TargetIsWindows, IsRemote ? null : File.Exists);
        if (!IsRemote && !File.Exists(executable))
            throw new FileNotFoundException($"TensorFold wasn't found at {executable}. Install a release from the Engines page.");

        var serve = TensorFoldPresetSettings.ServeArgs(preset);
        serve.AddRange(["--host", ListenAddress, "--port", Port.ToString(CultureInfo.InvariantCulture)]);

        if (!IsRemote && !TensorFoldLayout.IsRepoId(preset.ModelPath!) && !Directory.Exists(preset.ModelPath!.Trim()))
            throw new DirectoryNotFoundException($"The checkpoint folder {preset.ModelPath} doesn't exist. Enter a Hugging Face repo id (owner/name) or a folder on this machine.");
        if (!IsRemote && DrafterOf(serve) is { } drafter && !TensorFoldLayout.IsRepoId(drafter) && !Directory.Exists(drafter))
            throw new DirectoryNotFoundException($"The drafter folder {drafter} doesn't exist. Pick a drafter from the model library, or enter a repo id in the Hugging Face cache.");

        var environment = BuildEnvironment(preset);
        _maxConcurrent = TensorFoldPresetSettings.MaxConcurrent(preset);

        var workingDirectory = Path.GetDirectoryName(executable);
        if (TargetIsWindows || TensorFoldPresetSettings.PullRef(preset) is not { } pullRef)
            return new ServerLaunchSpec(executable, serve, IsRemote ? null : workingDirectory, environment);

        // sh -c SCRIPT $0 $1… — every value is a positional parameter, never parsed by the shell.
        List<string> args =
        [
            "-c", TensorFoldPresetSettings.PullThenServeScript, "sh",
            executable, pullRef, TensorFoldLayout.HubCacheFolderName(pullRef), TensorFoldPresetSettings.PullMode(preset),
            .. serve,
        ];
        return new ServerLaunchSpec("/bin/sh", args, IsRemote ? null : workingDirectory, environment);
    }

    /// <summary>
    /// The Intel XPU image in Docker Desktop: <c>docker run</c> is the process the wrapper watches, and removing
    /// the container is what stops it. A model folder on a local drive is mounted read-only; a repo id is pulled
    /// into the Hugging Face cache volume first, which vLLM's Intel image shares.
    /// </summary>
    private ServerLaunchSpec BuildDockerLaunchSpec(ModelPreset preset)
    {
        if (!TargetIsWindows || IsRemote)
            throw new InvalidOperationException("TensorFold's Intel XPU image runs in Docker Desktop on this Windows machine only, not on an agent.");
        if (!TensorFoldDocker.IsInstall(InstallFolderPath!))
            throw new FileNotFoundException($"The TensorFold image's build files aren't in {InstallFolderPath}. Install it from the Engines page.");

        var serve = TensorFoldPresetSettings.ServeArgs(preset);
        var modelFolder = DockerDesktop.ModelFolder(preset.ModelPath!);
        if (modelFolder is not null)
        {
            if (!Directory.Exists(modelFolder))
                throw new DirectoryNotFoundException($"The checkpoint folder {modelFolder} doesn't exist.");
            serve[1] = DockerDesktop.ContainerModelPath;
        }
        else if (!TensorFoldLayout.IsRepoId(preset.ModelPath!))
        {
            throw new InvalidOperationException($"'{preset.ModelPath}' is neither a Hugging Face repo id (owner/name) nor a folder on a local drive.");
        }

        // A drafter folder from the library is mounted beside the model; a repo id is looked up in the container's Hugging Face cache.
        string? drafterFolder = null;
        var drafterIndex = serve.IndexOf("--drafter") + 1;
        if (drafterIndex > 0 && drafterIndex < serve.Count && DockerDesktop.ModelFolder(serve[drafterIndex]) is { } hostDrafter)
        {
            if (!Directory.Exists(hostDrafter))
                throw new DirectoryNotFoundException($"The drafter folder {hostDrafter} doesn't exist.");
            drafterFolder = hostDrafter;
            serve[drafterIndex] = TensorFoldDocker.ContainerDrafterPath;
        }
        serve.AddRange(["--host", "0.0.0.0", "--port", Port.ToString(CultureInfo.InvariantCulture)]);

        var environment = BuildEnvironment(preset);
        _maxConcurrent = TensorFoldPresetSettings.MaxConcurrent(preset);

        List<string> run;
        if (modelFolder is null && TensorFoldPresetSettings.PullRef(preset) is { } pullRef)
        {
            List<string> script =
            [
                "-c", TensorFoldPresetSettings.PullThenServeScript, "sh",
                TensorFoldDocker.ContainerExecutable, pullRef, TensorFoldLayout.HubCacheFolderName(pullRef), TensorFoldPresetSettings.PullMode(preset),
                .. serve,
            ];
            run = TensorFoldDocker.RunArgs(Port, ListenAddress, null, environment.Keys, "/bin/sh", script, drafterFolder);
        }
        else
        {
            run = TensorFoldDocker.RunArgs(Port, ListenAddress, modelFolder, environment.Keys, null, serve, drafterFolder);
        }

        return new ServerLaunchSpec(DockerDesktop.Executable, run, null, environment,
            new ServerStopSpec(DockerDesktop.Executable, TensorFoldDocker.StopArgs(Port)));
    }

    /// <summary>The value after <c>--drafter</c> in the serve arguments, or null.</summary>
    private static string? DrafterOf(List<string> serve)
    {
        var i = serve.IndexOf("--drafter");
        return i >= 0 && i + 1 < serve.Count ? serve[i + 1] : null;
    }

    /// <summary>The preset's variables plus the Hugging Face token, for gated checkpoints.</summary>
    private Dictionary<string, string> BuildEnvironment(ModelPreset preset)
    {
        var environment = TensorFoldPresetSettings.EnvironmentOf(preset);
        if (HuggingFaceToken() is { Length: > 0 } token)
            environment["HF_TOKEN"] = token;
        return environment;
    }

    private string? HuggingFaceToken()
    {
        try
        {
            using var scope = ScopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetService<IModelLibrarySettingsService>();
            return settings?.GetAsync().GetAwaiter().GetResult().HuggingFaceApiToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Hugging Face token; gated checkpoints won't download.");
            return null;
        }
    }

    private int ResolveMaxConcurrentFromActivePreset()
    {
        if (ServerInstance?.ActivePresetId is not Guid presetId)
            return TensorFoldPresetSettings.DefaultMaxConcurrent;
        try
        {
            using var scope = ScopeFactory.CreateScope();
            var preset = scope.ServiceProvider.GetService<IPresetManager>()?.GetById(presetId);
            return preset is null ? TensorFoldPresetSettings.DefaultMaxConcurrent : TensorFoldPresetSettings.MaxConcurrent(preset);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the parallel count of preset {PresetId}", presetId);
            return TensorFoldPresetSettings.DefaultMaxConcurrent;
        }
    }

    // Readiness comes from /health (ReadyWhenHealthy), so nothing in the output has to be recognised.
    protected override bool IsModelLoadedLine(string line) => false;

    protected override int? TryParseListeningPort(string line) => null;

    /// <summary>
    /// TensorFold has no <c>/props</c>; its <c>/v1/models</c> entry carries <c>max_model_len</c>, the
    /// window the loaded engine accepts after fitting it to memory.
    /// </summary>
    protected override async Task<LlamaServerProps?> ReadServerPropsAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/v1/models");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (VllmProvider.PropsFromModels(doc.RootElement, TensorFoldPresetSettings.ServedModelName) is not { } props)
            return null;

        await LogProviderMessage(ServerLogLevel.Info, $"Server /v1/models: context {(props.MaxModelLength?.ToString("N0", CultureInfo.InvariantCulture) ?? "?")}.");
        return props;
    }

    protected override void PrepareChatRequestBody(Dictionary<string, JsonElement> body)
    {
        body["model"] = JsonSerializer.SerializeToElement(TensorFoldPresetSettings.ServedModelName);
        PrepareStreamOptions(body);
    }

    /// <summary>
    /// A streamed request asks for usage (<c>stream_options.include_usage</c>), which TensorFold sends in
    /// a final event of its own, so token counts are known; a request that doesn't stream drops it.
    /// </summary>
    public static void PrepareStreamOptions(Dictionary<string, JsonElement> body)
    {
        if (!(body.TryGetValue("stream", out var stream) && stream.ValueKind == JsonValueKind.True))
        {
            body.Remove("stream_options");
            return;
        }

        var options = body.TryGetValue("stream_options", out var existing) && existing.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(existing.GetRawText()) as JsonObject ?? []
            : [];
        options["include_usage"] = true;
        body["stream_options"] = JsonSerializer.SerializeToElement(options);
    }

    protected override string PrepareRawRequestPayload(string endpoint, string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is JsonObject root)
            {
                root["model"] = TensorFoldPresetSettings.ServedModelName;
                return root.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: TensorFold will answer it with an error the client can read.
        }
        return payload;
    }
}
