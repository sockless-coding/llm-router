using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Mock CPU backend provider for testing.
/// </summary>
public class MockCpuProvider : IBackendProvider
{
    private bool _isRunning;

    public BackendType SupportedBackend => BackendType.Cpu;

    public async Task<bool> StartProcessAsync(ModelPreset preset, CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        _isRunning = true;
        return true;
    }

    public async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning) return;
        await Task.Delay(300, cancellationToken);
        _isRunning = false;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken);
        return _isRunning;
    }

    public async Task<RouteResponse?> SendRequestAsync(string payload, CancellationToken cancellationToken = default)
    {
        if (!_isRunning) throw new InvalidOperationException("Server is not running.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(200, cancellationToken);
        var promptProcessingMs = sw.ElapsedMilliseconds;
        var promptTokens = Math.Max(1, payload.Length / 4);

        await Task.Delay(300, cancellationToken);
        var genMs = sw.ElapsedMilliseconds - promptProcessingMs;
        sw.Stop();

        return new RouteResponse
        {
            Payload = $"[Mock CPU Response] You sent: {payload}",
            PromptTokensProcessed = promptTokens,
            GeneratedTokenCount = 15,
            PromptProcessingMs = promptProcessingMs,
            GenerationMs = genMs,
            TotalLatencyMs = sw.ElapsedMilliseconds,
            FirstTokenLatencyMs = promptProcessingMs + 20,
        };
    }
}
