using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Models;

namespace LR.Providers.LlamaCpp;

/// <summary>
/// Runs llama.cpp's <c>llama-server</c>. Launch arguments come from <see cref="LlamaCppArgBuilder"/>;
/// per-request prompt/generation timings are scraped from the server's stdout by
/// <see cref="LlamaCppStdoutParser"/> and correlated to requests by
/// <see cref="LlamaCppTimingCoordinator"/>. Everything else is shared in
/// <see cref="ManagedServerProviderBase"/>.
/// </summary>
public partial class LlamaCppProvider : ManagedServerProviderBase
{
    public override ServerEngine Engine => ServerEngine.LlamaCpp;

    /// <summary>The <c>llama-server</c> executable name for this OS.</summary>
    public static string ServerExecutableName =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    private readonly LlamaCppArgBuilder _argBuilder = new();
    private readonly LlamaCppStdoutParser _stdoutParser = new();
    private readonly LlamaCppTimingCoordinator _timingCoordinator;
    private readonly ILogger<LlamaCppProvider> _logger;

    public LlamaCppProvider(
        ILogger<LlamaCppProvider> logger,
        ILogger<LlamaCppTimingCoordinator> timingLogger,
        IServiceScopeFactory scopeFactory)
        : base(logger, scopeFactory)
    {
        _logger = logger;
        _timingCoordinator = new LlamaCppTimingCoordinator(timingLogger);
    }

    protected override ServerLaunchSpec BuildLaunchSpec(ModelPreset preset)
    {
        string executablePath = ResolveServerExecutable("llama-server");

        _argBuilder.Port = Port;
        var args = _argBuilder.Build(preset);

        // A remote llama-server must accept the router's connections from the network; it binds
        // 127.0.0.1 by default. A preset that sets its own --host keeps it.
        if (IsRemote && string.IsNullOrWhiteSpace(preset.Host))
            args.AddRange(["--host", ListenAddress]);

        return new ServerLaunchSpec(executablePath, args, InstallFolderPath);
    }

    // Older builds log "main: model loaded" / "llama_server: model loaded"; current ones (b12xxx)
    // prefix a timestamp and a truncated function name: "... srv  server_run_i: model loaded".
    // Matched on the ": model loaded" suffix so either format is recognised.
    protected override bool IsModelLoadedLine(string line) => ModelLoadedRegex().IsMatch(line);

    protected override int? TryParseListeningPort(string line)
    {
        var match = ListeningOnRegex().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>Exposed for tests: whether a llama-server output line marks the model as loaded.</summary>
    public static bool IsModelLoadedMarker(string line) => ModelLoadedRegex().IsMatch(line);

    /// <summary>Exposed for tests: the port in a llama-server "listening on" line, or null.</summary>
    public static int? ParseListeningPort(string line) =>
        ListeningOnRegex().Match(line) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    protected override void OnOutputLine(string line)
    {
        var timingEvent = _stdoutParser.ParseLine(line);
        if (timingEvent != null)
            _timingCoordinator.ProcessEvent(timingEvent);
    }

    // Register each request for timing data collection from stdout.
    protected override void OnRequestStarting(RouteResponse response)
    {
        _timingCoordinator.EnqueuePending(DateTimeOffset.UtcNow, response);
        _logger.LogInformation("[Stats] Enqueued request.");
    }

    // By the time a response is complete, stdout parsing should have captured its timing.
    protected override void OnRequestCompleted(RouteResponse response)
    {
        _timingCoordinator.MergeTimingData(response);
        _logger.LogInformation("[Stats] After merge - PromptMs={PromptMs:F0}, GenMs={GenMs:F0}, TotalMs={TotalMs:F0}, TokensProcessed={Tokens}",
            response.PromptProcessingMs, response.GenerationMs, response.TotalLatencyMs, response.PromptTokensProcessed);
    }

    // llama.cpp's OpenAI-compatible endpoint has historically been driven by ?stream=true;
    // its Anthropic endpoint takes "stream" as a body field only (like the real Claude API).
    protected override string OpenAiStreamingEndpoint => "/v1/chat/completions?stream=true";

    [GeneratedRegex(@"\w+:\s+listening\s+on\s+https?://[^\s]*:(\d+)")]
    private static partial Regex ListeningOnRegex();

    [GeneratedRegex(@"\w+:\s+model loaded\s*$")]
    private static partial Regex ModelLoadedRegex();
}
