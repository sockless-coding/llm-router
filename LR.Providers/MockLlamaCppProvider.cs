using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Mock implementation of IBackendProvider for testing and development.
/// Simulates process lifecycle with configurable delays — no real llama.cpp required.
/// </summary>
public class MockLlamaCppProvider : IBackendProvider
{
    private bool _isRunning;
    private readonly int _startDelayMs;
    private readonly int _healthCheckDelayMs;

    public MockLlamaCppProvider(int startDelayMs = 500, int healthCheckDelayMs = 200)
    {
        _startDelayMs = startDelayMs;
        _healthCheckDelayMs = healthCheckDelayMs;
    }

    public BackendType SupportedBackend => BackendType.Cuda;

    public async Task<bool> StartProcessAsync(ModelPreset preset, CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return true;

        // Simulate startup delay
        await Task.Delay(_startDelayMs, cancellationToken);

        _isRunning = true;
        return true;
    }

    public async Task StopProcessAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            return;

        // Simulate graceful shutdown delay
        await Task.Delay(300, cancellationToken);
        _isRunning = false;
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(_healthCheckDelayMs, cancellationToken);
        return _isRunning;
    }

    public async Task<string?> SendRequestAsync(string payload, CancellationToken cancellationToken = default)
    {
        if (!_isRunning)
            throw new InvalidOperationException("Server is not running.");

        // Simulate inference delay
        await Task.Delay(500, cancellationToken);

        return $"[Mock Response] You sent: {payload}"
            + "\nThis is a simulated response from the mock Llama.cpp provider.";
    }
}
