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

    /// <summary>
    /// Shell command to initialize the environment before starting server processes.
    /// For example, "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64 for SYCL backends on Windows.
    /// </summary>
    protected string? EnvironmentSetupCommand { get; set; }

    public LlamaCppProvider(int port = 8080)
    {
        Port = port;
    }

    /// <summary>
    /// Applies engine-specific configuration from the backend config data.
    /// Override to add provider-specific configuration handling.
    /// </summary>
    public virtual void Configure(BackendConfigData configData)
    {
        ExecutableFolderPath = configData.LlamaCppExecutableFolderPath;
        CompanionAppPath = configData.CompanionAppPath;
        EnvironmentSetupCommand = configData.EnvironmentSetupCommand;
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
        var args = new List<string>();

        // --- Core settings ---
        args.Add("--model");
        args.Add(preset.ModelPath);
        AddIntArg(args, "--ctx-size", preset.ContextSize);
        AddIntArg(args, "--gpu-layers", preset.GpuLayers);
        AddArgIfSet(args, "--cache-type-k", preset.CacheTypeK);
        AddArgIfSet(args, "--cache-type-v", preset.CacheTypeV);
        AddArgIfSet(args, "--flash-attn", preset.FlashAttention);
        if (preset.Jinja.HasValue)
            args.Add(preset.Jinja.Value ? "--jinja" : "--no-jinja");
        AddArgIfSet(args, "--spec-type", preset.SpecType);

        // --- Sampling (core) ---
        AddFloatArg(args, "--temp", preset.Temperature);
        AddIntArg(args, "--top-k", preset.TopK);
        AddFloatArg(args, "--min-p", preset.MinP);
        AddFloatArg(args, "--top-p", preset.TopP);
        AddFloatArg(args, "--repeat-penalty", preset.RepeatPenalty);
        AddFloatArg(args, "--presence-penalty", preset.PresencePenalty);

        // --- Advanced: Generation ---
        AddIntArg(args, "--threads", preset.Threads);
        AddIntArg(args, "--threads-batch", preset.ThreadsBatch);
        AddIntArg(args, "--n-predict", preset.PredictN);
        AddIntArg(args, "--batch-size", preset.BatchSize);
        AddIntArg(args, "--ubatch-size", preset.UbatchSize);
        AddIntArg(args, "--keep", preset.KeepN);
        AddLongArg(args, "--seed", preset.Seed);
        if (preset.IgnoreEos.HasValue && preset.IgnoreEos.Value)
            args.Add("--ignore-eos");

        // --- Advanced: GPU/Device ---
        AddArgIfSet(args, "--device", preset.Device);
        AddArgIfSet(args, "--split-mode", preset.SplitMode);
        AddArgIfSet(args, "--tensor-split", preset.TensorSplit);
        AddIntArg(args, "--main-gpu", preset.MainGpu);
        AddArgIfSet(args, "--fit", preset.Fit);
        if (preset.KvOffload.HasValue)
            args.Add(preset.KvOffload.Value ? "--kv-offload" : "--no-kv-offload");
        if (preset.Repack.HasValue)
            args.Add(preset.Repack.Value ? "--repack" : "--no-repack");

        // --- Advanced: Memory ---
        AddArgIfSet(args, "--load-mode", preset.LoadMode);
        AddIntArg(args, "--cache-ram", preset.CacheRam);

        // --- Advanced: RoPE Scaling ---
        AddArgIfSet(args, "--rope-scaling", preset.RopeScalingType);
        AddFloatArg(args, "--rope-scale", preset.RopeScale);
        AddFloatArg(args, "--rope-freq-base", preset.RopeFreqBase);
        AddFloatArg(args, "--rope-freq-scale", preset.RopeFreqScale);
        AddIntArg(args, "--yarn-orig-ctx", preset.YarnOrigCtx);
        AddFloatArg(args, "--yarn-ext-factor", preset.YarnExtFactor);
        AddFloatArg(args, "--yarn-attn-factor", preset.YarnAttnFactor);
        AddFloatArg(args, "--yarn-beta-slow", preset.YarnBetaSlow);
        AddFloatArg(args, "--yarn-beta-fast", preset.YarnBetaFast);

        // --- Advanced: Sampling (Extended) ---
        AddFloatArg(args, "--top-n-sigma", preset.TopNSigma);
        AddFloatArg(args, "--xtc-probability", preset.XtcProbability);
        AddFloatArg(args, "--xtc-threshold", preset.XtcThreshold);
        AddFloatArg(args, "--typical-p", preset.TypicalP);
        AddIntArg(args, "--repeat-last-n", preset.RepeatLastN);
        AddFloatArg(args, "--frequency-penalty", preset.FrequencyPenalty);
        AddFloatArg(args, "--dry-multiplier", preset.DryMultiplier);
        AddFloatArg(args, "--dry-base", preset.DryBase);
        AddIntArg(args, "--dry-allowed-length", preset.DryAllowedLength);
        AddIntArg(args, "--dry-penalty-last-n", preset.DryPenaltyLastN);
        AddIntArg(args, "--mirostat", preset.Mirostat);
        AddFloatArg(args, "--mirostat-lr", preset.MirostatTau);
        AddFloatArg(args, "--mirostat-ent", preset.MirostatEta);
        AddFloatArg(args, "--dynatemp-range", preset.DynatempRange);
        AddFloatArg(args, "--dynatemp-exp", preset.DynatempExp);

        // --- Advanced: Speculative Decoding ---
        AddArgIfSet(args, "--spec-draft-model", preset.SpecDraftModel);
        AddIntArg(args, "--spec-draft-n-max", preset.SpecDraftNMax);
        AddIntArg(args, "--spec-draft-n-min", preset.SpecDraftNMin);
        AddFloatArg(args, "--draft-p-min", preset.SpecDraftPMin);
        AddArgIfSet(args, "--cache-type-k-draft", preset.SpecDraftTypeK);
        AddArgIfSet(args, "--cache-type-v-draft", preset.SpecDraftTypeV);
        AddIntArg(args, "--gpu-layers-draft", preset.SpecDraftGpuLayers);
        AddIntArg(args, "--spec-draft-threads", preset.SpecDraftThreads);

        // --- Advanced: Server ---
        AddArgIfSet(args, "--host", preset.Host);
        AddIntArg(args, "--port", preset.Port ?? Port);
        AddIntArg(args, "--parallel", preset.Parallel);
        if (preset.ContBatching.HasValue)
            args.Add(preset.ContBatching.Value ? "--cont-batching" : "--no-cont-batching");
        AddIntArg(args, "--timeout", preset.Timeout);
        AddArgIfSet(args, "--api-key", preset.ApiKey);
        if (preset.CachePrompt.HasValue)
            args.Add(preset.CachePrompt.Value ? "--cache-prompt" : "--no-cache-prompt");

        // --- Advanced: Reasoning ---
        AddArgIfSet(args, "--reasoning", preset.Reasoning);
        AddIntArg(args, "--reasoning-budget", preset.ReasoningBudget);

        // --- Advanced: Multimodal ---
        AddArgIfSet(args, "--mmproj", preset.Mmproj);
        AddIntArg(args, "--image-min-tokens", preset.ImageMinTokens);
        AddIntArg(args, "--image-max-tokens", preset.ImageMaxTokens);

        // --- Advanced: LoRA ---
        AddArgIfSet(args, "--lora", preset.Lora);

        // --- Advanced: Chat Template ---
        AddArgIfSet(args, "--chat-template", preset.ChatTemplate);

        // --- Fallback flags (from Flags dictionary) ---
        foreach (var flag in preset.Flags)
            args.Add(flag.Key);

        return args;
    }

    private static void AddArgIfSet(List<string> args, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            args.Add(name);
            args.Add(value);
        }
    }

    private static void AddIntArg(List<string> args, string name, int? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString());
        }
    }

    private static void AddLongArg(List<string> args, string name, long? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString());
        }
    }

    private static void AddFloatArg(List<string> args, string name, float? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString("G7"));
        }
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
    /// Initializes the environment by running the configured setup command.
    /// For example, runs "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64 for SYCL backends.
    /// 
    /// This method creates a temporary batch script that first runs the setup command,
    /// then launches the target executable. This ensures environment variables set by
    /// the init script (like oneAPI's setvars.bat) are inherited by subsequent processes.
    /// </summary>
    protected virtual async Task InitializeEnvironmentAsync(string executablePath, ProcessStartInfo startInfo, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(EnvironmentSetupCommand))
            return;

        // Build a temporary batch script that initializes the environment then runs the target executable.
        // This ensures env vars set by scripts like oneAPI's setvars.bat are inherited.
        string tempBatchPath = Path.Combine(Path.GetTempPath(), $"llm-router-init-{Guid.NewGuid():N}.bat");

        try
        {
            var lines = new List<string>
            {
                "@echo off",
                // Run the environment setup command (e.g., setvars.bat intel64)
                EnvironmentSetupCommand,
                // Launch the target executable and pass through its exit code
                $"call \"{executablePath}\" {startInfo.Arguments ?? string.Empty}",
            };

            await File.WriteAllLinesAsync(tempBatchPath, lines, ct);

            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c \"" + tempBatchPath + "\"";
            // When using cmd.exe /c with a batch file, UseShellExecute must be false for redirection to work.
            startInfo.UseShellExecute = false;
        }
        catch
        {
            // If we can't create the temp script, fall through without environment setup.
            // The process will still launch, just without initialized env vars.
        }
    }

    /// <summary>
    /// Cleans up temporary batch scripts created by InitializeEnvironmentAsync.
    /// </summary>
    protected void CleanupTempBatch(string? tempPath)
    {
        if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
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
            var startInfo = new ProcessStartInfo
            {
                FileName = CompanionAppPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // If environment setup is configured, wrap the companion app launch in a script
            // that first initializes the environment (e.g., oneAPI setvars.bat)
            await InitializeEnvironmentAsync(CompanionAppPath, startInfo, ct);

            _companionProcess = new Process { StartInfo = startInfo };
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
