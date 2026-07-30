using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Mock CPU backend provider for testing.
/// Inherits from LlamaCppProvider since it's a llama.cpp build compiled for CPU.
/// </summary>
public class MockCpuProvider : LlamaCppProvider
{
    private bool _isRunning;

    public MockCpuProvider(int port = 8080) : base(port)
    {
        GpuBackendType = BackendType.Cpu;
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

    public override async IAsyncEnumerable<RouteStreamChunk> SendStreamRequestAsync(
        string payload, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_isRunning) throw new InvalidOperationException("Server is not running.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(200, cancellationToken);
        var promptProcessingMs = sw.ElapsedMilliseconds;
        var promptTokens = Math.Max(1, payload.Length / 4);

        foreach (var word in new[] { "Hello", ",", " this", " is", " a", " CPU", " response" })
        {
            if (cancellationToken.IsCancellationRequested) break;
            yield return new RouteStreamChunk { TextDelta = word + " " };
            await Task.Delay(60, cancellationToken);
        }

        var genMs = sw.ElapsedMilliseconds - promptProcessingMs;
        sw.Stop();
        yield return new RouteStreamChunk
        {
            IsFinal = true,
            Response = new RouteResponse
            {
                Payload = "Hello, this is a CPU response",
                PromptTokensProcessed = promptTokens,
                GeneratedTokenCount = 7,
                PromptProcessingMs = promptProcessingMs,
                GenerationMs = genMs,
                TotalLatencyMs = sw.ElapsedMilliseconds,
                FirstTokenLatencyMs = promptProcessingMs + 20,
            }
        };
    }
}
