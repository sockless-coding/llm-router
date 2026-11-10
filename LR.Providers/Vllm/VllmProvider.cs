using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.Vllm;

/// <summary>
/// Runs vLLM's <c>vllm serve</c> (github.com/vllm-project/vllm): OpenAI-compatible
/// <c>/v1/chat/completions</c> and Anthropic-compatible <c>/v1/messages</c>, with a <c>/health</c> but
/// no llama.cpp-style <c>/props</c> or <c>/slots</c>. Launched directly on Linux and through
/// <c>wsl.exe</c> on Windows (see <see cref="VllmLayout"/>); WSL forwards loopback, so the router talks
/// to it on 127.0.0.1 either way. Three things differ from llama.cpp:
/// <list type="bullet">
/// <item>Readiness comes from <c>/health</c> rather than log lines: the server only listens once the
/// model is loaded, whereas log wording changes between vLLM versions and frontends.</item>
/// <item>vLLM rejects a request whose <c>model</c> isn't a name it serves, and clients ask by preset
/// name or alias. Every server is started under one fixed name and each request is rewritten to it.</item>
/// <item>A first start can download the whole model, so the startup timeout is hours, not minutes.</item>
/// </list>
/// </summary>
public class VllmProvider : ManagedServerProviderBase
{
    public override ServerEngine Engine => ServerEngine.Vllm;

    /// <summary>In-flight limit when a preset sets none. vLLM batches and queues requests itself, so this is only a backstop.</summary>
    public const int DefaultMaxConcurrentRequests = 64;

    /// <summary>How long to wait after vLLM says it's up before concluding the router just can't reach it.</summary>
    private static readonly TimeSpan UnreachableAfter = TimeSpan.FromSeconds(60);

    private readonly ILogger<VllmProvider> _logger;
    private volatile int _maxConcurrent;
    private DateTime? _reportedUpAt;

    public VllmProvider(ILogger<VllmProvider> logger, IServiceScopeFactory scopeFactory)
        : base(logger, scopeFactory)
    {
        _logger = logger;
    }

    protected override TimeSpan StartupTimeout => TimeSpan.FromHours(4);

    // Large models on a queued batch can go a long time between chunks.
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
            throw new InvalidOperationException("vLLM's install folder is not set — bind the server to a vLLM install from the Engines page.");

        if (VllmDocker.IsDockerFolder(InstallFolderPath))
            return BuildDockerLaunchSpec(preset);

        var command = VllmLayout.Resolve(InstallFolderPath, TargetIsWindows, IsRemote ? null : File.Exists);
        if (!IsRemote && command.Wsl is null && !File.Exists(command.Executable))
            throw new FileNotFoundException($"vLLM wasn't found at {command.Executable}. Install it from the Engines page.");
        if (!IsRemote && command.Wsl is not null && !VllmLayout.IsInstall(InstallFolderPath))
            throw new FileNotFoundException($"vLLM wasn't found in {command.Wsl.LinuxPath} in WSL distro '{command.Wsl.Distro}'. If this router runs as a service, WSL distros of your user account aren't visible to it.");

        var args = new List<string>(command.PrefixArgs);
        args.AddRange(VllmPresetSettings.ServeArgs(preset, targetIsWsl: command.Wsl is not null));
        args.AddRange(["--host", ListenAddress, "--port", Port.ToString(CultureInfo.InvariantCulture)]);

        var environment = BuildEnvironment(preset, command.Wsl is not null);
        _maxConcurrent = VllmPresetSettings.MaxConcurrent(preset) ?? DefaultMaxConcurrentRequests;
        _reportedUpAt = null;

        return new ServerLaunchSpec(command.Executable, args, command.WorkingDirectory, environment);
    }

    /// <summary>
    /// A server in a container: <c>docker run</c> is the process the wrapper watches. Inside, the server
    /// listens on every interface of the container, and the address it's reachable on is decided by what's
    /// published on the host.
    /// </summary>
    private ServerLaunchSpec BuildDockerLaunchSpec(ModelPreset preset)
    {
        if (!TargetIsWindows)
            throw new InvalidOperationException("vLLM on an Intel GPU is only set up for Windows with Docker Desktop.");
        if (!IsRemote && !VllmDocker.IsInstall(InstallFolderPath!))
            throw new FileNotFoundException($"The vLLM image's build files aren't in {InstallFolderPath}. Install it from the Engines page.");

        var serve = VllmPresetSettings.ServeArgs(preset);
        var modelFolder = VllmDocker.ModelFolder(preset.ModelPath!);
        if (modelFolder is not null)
        {
            if (!IsRemote && !Directory.Exists(modelFolder))
                throw new DirectoryNotFoundException($"The model folder {modelFolder} doesn't exist.");
            serve[1] = VllmDocker.ContainerModelPath;
        }
        serve.AddRange(["--host", "0.0.0.0", "--port", Port.ToString(CultureInfo.InvariantCulture)]);

        var environment = BuildEnvironment(preset, throughWsl: false);
        _maxConcurrent = VllmPresetSettings.MaxConcurrent(preset) ?? DefaultMaxConcurrentRequests;
        _reportedUpAt = null;

        return new ServerLaunchSpec(
            VllmDocker.Executable,
            VllmDocker.RunArgs(Port, ListenAddress, modelFolder, environment.Keys, serve),
            null,
            environment,
            new ServerStopSpec(VllmDocker.Executable, VllmDocker.StopArgs(Port)));
    }

    /// <summary>
    /// The preset's variables plus the Hugging Face token. A Linux process inside WSL only gets the
    /// Windows variables listed in <c>WSLENV</c>, so that's filled in too.
    /// </summary>
    private Dictionary<string, string> BuildEnvironment(ModelPreset preset, bool throughWsl)
    {
        var environment = VllmPresetSettings.EnvironmentOf(preset);

        if (!environment.ContainsKey("HF_TOKEN") && HuggingFaceToken() is { Length: > 0 } token)
            environment["HF_TOKEN"] = token;

        if (throughWsl && environment.Count > 0)
            environment["WSLENV"] = string.Join(':', environment.Keys);

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
            _logger.LogWarning(ex, "Could not read the Hugging Face token; gated models won't download.");
            return null;
        }
    }

    private int ResolveMaxConcurrentFromActivePreset()
    {
        if (ServerInstance?.ActivePresetId is not Guid presetId)
            return DefaultMaxConcurrentRequests;
        try
        {
            using var scope = ScopeFactory.CreateScope();
            var preset = scope.ServiceProvider.GetService<IPresetManager>()?.GetById(presetId);
            return (preset is null ? null : VllmPresetSettings.MaxConcurrent(preset)) ?? DefaultMaxConcurrentRequests;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the concurrency limit of preset {PresetId}", presetId);
            return DefaultMaxConcurrentRequests;
        }
    }

    // Readiness comes from /health (ReadyWhenHealthy), so nothing in the output has to be recognised.
    protected override bool IsModelLoadedLine(string line) => false;

    protected override int? TryParseListeningPort(string line) => null;

    protected override void OnOutputLine(string line)
    {
        // Only the wording for "the HTTP server is up" that the unreachable-backend check below uses.
        if (_reportedUpAt is null && line.Contains("Application startup complete", StringComparison.Ordinal))
            _reportedUpAt = DateTime.UtcNow;
    }

    protected override void OnReadinessCheckFailed()
    {
        if (!IsRemote || _reportedUpAt is not { } upAt || DateTime.UtcNow - upAt < UnreachableAfter)
            return;

        throw new InvalidOperationException(
            $"vLLM says it's running, but the router can't reach it at {BaseUrl}. Check the agent's firewall. " +
            (TargetIsWindows
                ? "A WSL server is only reachable from other machines with networkingMode=mirrored in the agent user's .wslconfig and the port allowed through the Hyper-V firewall."
                : ""));
    }

    /// <summary>
    /// vLLM has no <c>/props</c>; its <c>/v1/models</c> entry carries <c>max_model_len</c>, the context
    /// window it actually resolved (which differs from the preset's when that says <c>auto</c>, or
    /// when vLLM capped it to what fits in memory).
    /// </summary>
    protected override async Task<LlamaServerProps?> ReadServerPropsAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "/v1/models");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (PropsFromModels(doc.RootElement) is not { } props)
            return null;

        await LogProviderMessage(ServerLogLevel.Info, $"Server /v1/models: max_model_len {(props.MaxModelLength?.ToString("N0", CultureInfo.InvariantCulture) ?? "?")}.");
        return props;
    }

    /// <summary>The served model's facts from a <c>/v1/models</c> response, or null when it lists none.</summary>
    public static LlamaServerProps? PropsFromModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        // The router serves one model under VllmLayout.ServedModelName; take that entry, else the first.
        var model = data.EnumerateArray().FirstOrDefault(m =>
            m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == VllmLayout.ServedModelName);
        if (model.ValueKind != JsonValueKind.Object)
            model = data.EnumerateArray().FirstOrDefault();
        if (model.ValueKind != JsonValueKind.Object)
            return null;

        int? maxLen = model.TryGetProperty("max_model_len", out var len) && len.TryGetInt32(out var n) && n > 0 ? n : null;
        string? path = model.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

        return new LlamaServerProps
        {
            MaxModelLength = maxLen,
            // vLLM has no slots: any one request may use the whole window.
            ContextSizePerSlot = maxLen,
            ModelPath = path,
        };
    }

    protected override void PrepareChatRequestBody(Dictionary<string, JsonElement> body) =>
        body["model"] = JsonSerializer.SerializeToElement(VllmLayout.ServedModelName);

    protected override string PrepareRawRequestPayload(string endpoint, string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is JsonObject root)
            {
                root["model"] = VllmLayout.ServedModelName;
                return root.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Not JSON: vLLM will answer it with an error the client can read.
        }
        return payload;
    }
}
