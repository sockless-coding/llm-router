using System.Globalization;
using System.Runtime.CompilerServices;
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

    /// <summary>1 when the running preset decodes speculatively (MTP, EAGLE, a draft model…), 2 when not, 0 not yet known.</summary>
    private volatile int _speculative;

    /// <summary>Requests being measured for speculative-decoding counts, and the one per response.</summary>
    private readonly List<SpecDecodeProbe> _probesInFlight = [];
    private readonly ConditionalWeakTable<RouteResponse, SpecDecodeProbe> _probes = new();

    private static readonly TimeSpan MetricsTimeout = TimeSpan.FromSeconds(2);

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
        _speculative = IsSpeculative(preset) ? 1 : 2;
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
        _speculative = IsSpeculative(preset) ? 1 : 2;
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

    protected override void PrepareChatRequestBody(Dictionary<string, JsonElement> body)
    {
        body["model"] = JsonSerializer.SerializeToElement(VllmLayout.ServedModelName);
        PrepareStreamOptions(body);
    }

    /// <summary>
    /// vLLM rejects <c>stream_options</c> on a request that doesn't stream (a payload built for a
    /// streaming request can end up sent whole), so it's dropped there. On a streamed request it
    /// asks for usage on every frame (<c>continuous_usage_stats</c>): under speculative/MTP decoding
    /// one frame carries several tokens, and the running count is the only way to tell how many.
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
        options["continuous_usage_stats"] = true;
        body["stream_options"] = JsonSerializer.SerializeToElement(options);
    }

    // --- Speculative decoding counts -------------------------------------------------------------
    //
    // vLLM reports speculative decoding only as server-wide Prometheus counters, not per request.
    // Reading them before and after a request gives that request's counts — as long as nothing else
    // ran on the server meanwhile, so a request that overlapped another gets none.

    private sealed class SpecDecodeProbe
    {
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public SpecDecodeCounters? Before { get; set; }
        public bool Shared { get; set; }
    }

    /// <summary>vLLM's cumulative speculative-decoding counters (summed over engines), plus the requests it has running or waiting.</summary>
    public sealed record SpecDecodeCounters(double Drafts, double DraftTokens, double AcceptedTokens, double RequestsInFlight);

    protected override async Task OnRequestStartingAsync(RouteResponse response, CancellationToken cancellationToken)
    {
        if (!IsSpeculativeServer())
            return;

        var probe = new SpecDecodeProbe();
        lock (_probesInFlight)
        {
            // A request that failed never completes; don't let it count as overlapping forever.
            _probesInFlight.RemoveAll(p => DateTime.UtcNow - p.StartedAt > RequestTimeout);
            foreach (var other in _probesInFlight)
                other.Shared = true;
            probe.Shared = _probesInFlight.Count > 0;
            _probesInFlight.Add(probe);
        }
        _probes.AddOrUpdate(response, probe);

        var before = await ReadSpecDecodeCountersAsync(cancellationToken);
        lock (_probesInFlight)
        {
            probe.Before = before;
            // Something else (another client, a request from before a router restart) is on the server.
            if (before is { RequestsInFlight: > 0 })
                probe.Shared = true;
        }
    }

    protected override async Task OnRequestCompletedAsync(RouteResponse response)
    {
        if (!_probes.TryGetValue(response, out var probe))
            return;
        _probes.Remove(response);
        lock (_probesInFlight)
        {
            _probesInFlight.Remove(probe);
            if (probe.Shared || probe.Before is null)
                return;
        }

        if (await ReadSpecDecodeCountersAsync(CancellationToken.None) is { } after)
            ApplySpecDecodeDelta(response, probe.Before, after);
    }

    /// <summary>Sets the response's draft counts from the change in the counters across it.</summary>
    public static void ApplySpecDecodeDelta(RouteResponse response, SpecDecodeCounters before, SpecDecodeCounters after)
    {
        var drafts = after.Drafts - before.Drafts;
        var draftTokens = after.DraftTokens - before.DraftTokens;
        var accepted = after.AcceptedTokens - before.AcceptedTokens;
        // A restart resets the counters; nothing drafted means nothing to report.
        if (draftTokens <= 0 || accepted < 0 || accepted > draftTokens)
            return;

        response.DraftGenerated = (int)Math.Round(draftTokens);
        response.DraftAccepted = (int)Math.Round(accepted);
        response.DraftAcceptanceRate = accepted / draftTokens;
        // vLLM's "mean acceptance length": tokens per verification step, the bonus token included.
        if (drafts > 0)
            response.DraftMeanLen = 1 + accepted / drafts;
    }

    private async Task<SpecDecodeCounters?> ReadSpecDecodeCountersAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(MetricsTimeout);
            var (status, body) = await SendServerRequestAsync(HttpMethod.Get, "/metrics", null, cts.Token);
            return status == System.Net.HttpStatusCode.OK ? ParseSpecDecodeCounters(body) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            _logger.LogDebug(ex, "Could not read vLLM's /metrics");
            return null;
        }
    }

    /// <summary>
    /// The speculative-decoding counters from a Prometheus <c>/metrics</c> text, summed over label
    /// sets (one per engine), or null when the server exposes none (it isn't decoding speculatively).
    /// </summary>
    public static SpecDecodeCounters? ParseSpecDecodeCounters(string metrics)
    {
        double drafts = 0, draftTokens = 0, accepted = 0, inFlight = 0;
        bool found = false;
        foreach (var raw in metrics.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.IsEmpty || line[0] == '#')
                continue;

            int nameEnd = line.IndexOfAny('{', ' ');
            int valueStart = line.LastIndexOf(' ');
            if (nameEnd <= 0 || valueStart <= 0
                || !double.TryParse(line[(valueStart + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            switch (line[..nameEnd])
            {
                case "vllm:spec_decode_num_drafts_total": drafts += value; break;
                case "vllm:spec_decode_num_draft_tokens_total": draftTokens += value; found = true; break;
                case "vllm:spec_decode_num_accepted_tokens_total": accepted += value; break;
                case "vllm:num_requests_running":
                case "vllm:num_requests_waiting": inFlight += value; break;
            }
        }
        return found ? new SpecDecodeCounters(drafts, draftTokens, accepted, inFlight) : null;
    }

    private bool IsSpeculativeServer()
    {
        if (_speculative == 0)
        {
            bool speculative = false;
            if (ServerInstance?.ActivePresetId is Guid presetId)
            {
                try
                {
                    using var scope = ScopeFactory.CreateScope();
                    var preset = scope.ServiceProvider.GetService<IPresetManager>()?.GetById(presetId);
                    speculative = preset is not null && IsSpeculative(preset);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read preset {PresetId}", presetId);
                }
            }
            _speculative = speculative ? 1 : 2;
        }
        return _speculative == 1;
    }

    /// <summary>Whether the preset's command line turns on speculative decoding.</summary>
    public static bool IsSpeculative(ModelPreset preset) =>
        VllmPresetSettings.ServeArgs(preset).Any(a =>
            a.StartsWith("--speculative-config", StringComparison.Ordinal)
            || a.StartsWith("--speculative_config", StringComparison.Ordinal));

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
