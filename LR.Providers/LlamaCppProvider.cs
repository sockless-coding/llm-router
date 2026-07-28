using System.Diagnostics;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Abstract base class for llama.cpp-based backend providers.
/// Defines the contract and common utilities for real llama.cpp implementations.
/// </summary>
public abstract class LlamaCppProvider : IBackendProvider
{
    public ServerEngine Engine => ServerEngine.LlamaCpp;

    /// <summary>
    /// Path to the folder containing the llama.cpp server executable (e.g., "llama-server").
    /// Each GPU backend build (CUDA, Vulkan, SYCL) should be in its own folder.
    /// </summary>
    protected string? ExecutableFolderPath { get; set; }

    /// <summary>
    /// Path to the server executable within the folder (e.g., "llama-server.exe" on Windows).
    /// Override for engine-specific defaults.
    /// </summary>
    protected virtual string ServerExecutableName =>
        OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    /// <summary>
    /// Full path to the server executable (computed from ExecutableFolderPath + ServerExecutableName).
    /// </summary>
    protected string? ServerExecutablePath
    {
        get
        {
            if (string.IsNullOrEmpty(ExecutableFolderPath)) return null;
            return Path.Combine(ExecutableFolderPath, ServerExecutableName);
        }
    }

    /// <summary>
    /// The port this instance is listening on.
    /// </summary>
    protected int Port { get; private set; }

    /// <summary>
    /// Base URL of the running server (set after StartProcessAsync).
    /// Override or implement in concrete providers.
    /// </summary>
    protected virtual string? ServerUrl => $"http://localhost:{Port}";

    /// <summary>
    /// The GPU backend type this llama.cpp build was compiled for (e.g., CUDA, Vulkan, SYCL).
    /// Can be auto-detected from the folder name or set explicitly.
    /// </summary>
    protected BackendType? GpuBackendType { get; set; }

    /// <summary>
    /// The main server process handle (set after StartProcessAsync).
    /// </summary>
    private Process? _serverProcess;

    /// <summary>
    /// Companion application process (e.g., SYCL VRAM keeper on Windows without display connected).
    /// Set when a companion app is configured and started.
    /// </summary>
    private Process? _companionProcess;

    /// <summary>
    /// Path to the companion application executable, if any.
    /// </summary>
    protected string? CompanionAppPath { get; set; }

    public LlamaCppProvider(int port = 8080)
    {
        Port = port;
    }

    public abstract Task<bool> StartProcessAsync(ModelPreset preset, CancellationToken cancellationToken = default);
    public abstract Task StopProcessAsync(CancellationToken cancellationToken = default);
    public abstract Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);
    public abstract Task<RouteResponse?> SendRequestAsync(string payload, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Starts the companion application if one is configured.
    /// Override for backend-specific companion app behavior (e.g., SYCL VRAM keeper on Windows).
    /// </summary>
    protected virtual async Task StartCompanionAppAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(CompanionAppPath) || !File.Exists(CompanionAppPath))
            return;

        try
        {
            _companionProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = CompanionAppPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            _companionProcess.Start();
        }
        catch
        {
            // Log error but don't block server startup if companion app fails
            _companionProcess = null;
        }
    }

    /// <summary>
    /// Stops the companion application if it's running.
    /// </summary>
    protected virtual async Task StopCompanionAppAsync(CancellationToken ct = default)
    {
        if (_companionProcess is not null && !_companionProcess.HasExited)
        {
            try
            {
                _companionProcess.Kill();
                await _companionProcess.WaitForExitAsync(ct);
            }
            catch { /* Ignore errors on companion app shutdown */ }
            finally
            {
                _companionProcess?.Dispose();
                _companionProcess = null;
            }
        }
    }

    /// <summary>
    /// Stops the main server process if it's running.
    /// </summary>
    protected virtual async Task StopServerProcessAsync(CancellationToken ct = default)
    {
        if (_serverProcess is not null && !_serverProcess.HasExited)
        {
            try
            {
                _serverProcess.Kill();
                await _serverProcess.WaitForExitAsync(ct);
            }
            catch { /* Ignore errors on server shutdown */ }
            finally
            {
                _serverProcess?.Dispose();
                _serverProcess = null;
            }
        }
    }
}
