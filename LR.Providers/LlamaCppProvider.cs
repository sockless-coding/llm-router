using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Abstract base class for llama.cpp-based backend providers.
/// Defines the contract and common utilities for real llama.cpp implementations.
/// </summary>
public abstract class LlamaCppProvider : IBackendProvider
{
    public BackendType SupportedBackend { get; protected set; }

    /// <summary>
    /// Path to the llama.cpp server executable (e.g., "llama-server").
    /// Override or inject via configuration.
    /// </summary>
    protected string? ServerExecutablePath { get; set; }

    /// <summary>
    /// The port this instance is listening on.
    /// </summary>
    protected int Port { get; private set; }

    /// <summary>
    /// Base URL of the running server (set after StartProcessAsync).
    /// Override or implement in concrete providers.
    /// </summary>
    protected virtual string? ServerUrl => $"http://localhost:{Port}";

    public LlamaCppProvider(BackendType backendType, int port = 8080)
    {
        SupportedBackend = backendType;
        Port = port;
    }

    public abstract Task<bool> StartProcessAsync(ModelPreset preset, CancellationToken cancellationToken = default);
    public abstract Task StopProcessAsync(CancellationToken cancellationToken = default);
    public abstract Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public abstract Task<string?> SendRequestAsync(string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the command-line arguments from a ModelPreset.
    /// Override to add backend-specific flags.
    /// </summary>
    protected virtual List<string> BuildArgs(ModelPreset preset)
    {
        var args = new List<string>
        {
            "--model", preset.ModelPath,
            "--ctx-size", preset.ContextLength.ToString(),
            "--gpu-layers", preset.GpuLayers.ToString(),
            "--port", Port.ToString(),
        };

        // Add custom flags from the preset
        foreach (var flag in preset.Flags)
            args.Add(flag.Key);

        return args;
    }

    /// <summary>
    /// Sends a request to the llama.cpp server's completion endpoint.
    /// Override for backend-specific API differences.
    /// </summary>
    protected virtual async Task<string?> SendCompletionAsync(string payload, CancellationToken ct = default)
    {
        // TODO: Implement HTTP client call to ServerUrl/v1/completions
        throw new NotImplementedException("Not implemented in base class. Override in concrete provider.");
    }
}
