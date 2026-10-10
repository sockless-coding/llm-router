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
        if (string.IsNullOrEmpty(InstallFolderPath))
            throw new InvalidOperationException("Server executable path is not set — configure the server's llama.cpp folder or bind it to a managed build.");

        string executablePath = Path.Combine(InstallFolderPath, ServerExecutableName);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"Server executable not found at: {executablePath}");

        _argBuilder.Port = Port;
        return new ServerLaunchSpec(executablePath, _argBuilder.Build(preset), InstallFolderPath);
    }

    protected override bool IsModelLoadedLine(string line) =>
        line.Contains("llama_server: model loaded");

    protected override int? TryParseListeningPort(string line)
    {
        if (!line.Contains("llama_server: listening on"))
            return null;

        var match = ListeningOnRegex().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

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

    [GeneratedRegex(@"listening\s+on\s+http://[^:]+:(\d+)")]
    private static partial Regex ListeningOnRegex();
}
