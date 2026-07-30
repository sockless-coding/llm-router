using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Mock implementation of LlamaCppProvider for testing and development.
/// Simulates process lifecycle with configurable delays — no real llama.cpp required.
/// </summary>
public class MockLlamaCppProvider : LlamaCppProvider
{
    private bool _isRunning;
    private readonly int _startDelayMs;
    private readonly int _healthCheckDelayMs;

    public MockLlamaCppProvider(int startDelayMs = 500, int healthCheckDelayMs = 200) : base()
    {
        _startDelayMs = startDelayMs;
        _healthCheckDelayMs = healthCheckDelayMs;
    }

    public override async Task<bool> StartProcessAsync(ModelPreset preset, int? port = null, CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return true;

        // Apply the port from the server instance if provided
        Port = port ?? Port;

        // Simulate startup delay
        await Task.Delay(_startDelayMs, cancellationToken);

        // Start companion app if configured (e.g., SYCL VRAM keeper on Windows)
        await StartCompanionAppAsync(cancellationToken);

        _isRunning = true;
        return true;
    }

    public override async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            return;

        // Stop companion app first
        await StopCompanionAppAsync(cancellationToken);

        // Simulate graceful shutdown delay
        await Task.Delay(300, cancellationToken);
        _isRunning = false;
    }

    public override async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_healthCheckDelayMs, cancellationToken);
        return _isRunning;
    }

    public override async Task<RouteResponse?> SendRequestAsync(string payload, CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Server is not running.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Simulate prompt processing delay
        await Task.Delay(150, cancellationToken);
        var promptProcessingMs = sw.ElapsedMilliseconds;

        // Estimate tokens from payload length (roughly 4 chars per token)
        var promptTokens = Math.Max(1, payload.Length / 4);

        // Simulate generation delay (simulate ~20 output tokens at ~5ms each)
        await Task.Delay(100, cancellationToken);
        var genMs = sw.ElapsedMilliseconds - promptProcessingMs;

        sw.Stop();

        return new RouteResponse
        {
            Payload = $"[Mock Response] You sent: {payload}\nThis is a simulated response from the mock Llama.cpp provider.",
            PromptTokensProcessed = promptTokens,
            GeneratedTokenCount = 20,
            PromptProcessingMs = promptProcessingMs,
            GenerationMs = genMs,
            TotalLatencyMs = sw.ElapsedMilliseconds,
            FirstTokenLatencyMs = promptProcessingMs + 10,
        };
    }

    public override async IAsyncEnumerable<RouteStreamChunk> SendStreamRequestAsync(
        string payload, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Server is not running.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Simulate prompt processing delay (TTFT)
        await Task.Delay(150, cancellationToken);
        var promptProcessingMs = sw.ElapsedMilliseconds;

        // Estimate tokens from payload length (roughly 4 chars per token)
        var promptTokens = Math.Max(1, payload.Length / 4);

        // Simulate streaming generation (~20 output tokens at ~5ms each)
        var words = new[] { "Hello", ",", " this", " is", " a", " simulated", " response", " from", " the",
                            "mock", " Llama.cpp", " provider", "." };

        foreach (var word in words)
        {
            if (cancellationToken.IsCancellationRequested) break;
            yield return new RouteStreamChunk { TextDelta = word + " " };
            await Task.Delay(50, cancellationToken);
        }

        var genMs = sw.ElapsedMilliseconds - promptProcessingMs;
        sw.Stop();

        // Final chunk with metadata
        yield return new RouteStreamChunk
        {
            IsFinal = true,
            Response = new RouteResponse
            {
                Payload = string.Join("", words.Select(w => w + " ")),
                PromptTokensProcessed = promptTokens,
                GeneratedTokenCount = words.Length,
                PromptProcessingMs = promptProcessingMs,
                GenerationMs = genMs,
                TotalLatencyMs = sw.ElapsedMilliseconds,
                FirstTokenLatencyMs = promptProcessingMs + 10,
            }
        };
    }
}
