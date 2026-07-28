using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Mock SYCL backend provider for testing.
/// Inherits from LlamaCppProvider since it's a llama.cpp build compiled for SYCL.
/// </summary>
public class MockSyclProvider : LlamaCppProvider
{
    private bool _isRunning;

    public MockSyclProvider(int port = 8080) : base(port)
    {
        GpuBackendType = BackendType.Sycl;
    }

    public override async Task<bool> StartProcessAsync(ModelPreset preset, CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken);
        _isRunning = true;
        return true;
    }

    public override async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning) return;
        await Task.Delay(300, cancellationToken);
        _isRunning = false;
    }

    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken);
        return _isRunning;
    }

    public override async Task<RouteResponse?> SendRequestAsync(string payload, CancellationToken cancellationToken = default)
    {
        if (!_isRunning) throw new InvalidOperationException("Server is not running.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(130, cancellationToken);
        var promptProcessingMs = sw.ElapsedMilliseconds;
        var promptTokens = Math.Max(1, payload.Length / 4);

        await Task.Delay(90, cancellationToken);
        var genMs = sw.ElapsedMilliseconds - promptProcessingMs;
        sw.Stop();

        return new RouteResponse
        {
            Payload = $"[Mock SYCL Response] You sent: {payload}",
            PromptTokensProcessed = promptTokens,
            GeneratedTokenCount = 22,
            PromptProcessingMs = promptProcessingMs,
            GenerationMs = genMs,
            TotalLatencyMs = sw.ElapsedMilliseconds,
            FirstTokenLatencyMs = promptProcessingMs + 10,
        };
    }
}
