using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.Strata;

/// <summary>
/// Runs Strata (github.com/Niko1221/Strata): a Python HTTP frontend (<c>serve/server.py</c>) that
/// drives Strata's native engine and serves OpenAI-compatible <c>/v1/chat/completions</c>,
/// Anthropic-compatible <c>/v1/messages</c>, and llama.cpp-style <c>/health</c>, <c>/props</c>
/// and <c>/slots</c> — so request handling, health and capacity reporting are all inherited.
///
/// Strata keeps its model settings (GGUF, context, KV/offload, GPU split, sampling defaults) in
/// a per-model run config, <c>strata-&lt;model&gt;.json</c>, written by its own setup
/// (<c>START-HERE.bat</c> / <c>setup.sh</c>). A preset for a Strata server therefore points its
/// <see cref="ModelPreset.ModelPath"/> at that run config rather than at a GGUF; the llama.cpp
/// launch settings on the preset don't apply. The server is launched the same way Strata's own
/// <c>run-&lt;model&gt;</c> scripts do, but bound to loopback on the router-assigned port.
/// </summary>
public partial class StrataProvider : ManagedServerProviderBase
{
    public override ServerEngine Engine => ServerEngine.Strata;

    /// <summary>Environment variable Strata reads its API key from when <c>--api-key</c> isn't given.</summary>
    private const string ApiKeyEnvironmentVariable = "STRATA_API_KEY";

    private readonly ILogger<StrataProvider> _logger;

    /// <summary>
    /// API key the running server requires (from <see cref="ApiKeyEnvironmentVariable"/> or the run
    /// config's <c>api_key</c>), null if none. Resolved on start, or lazily from the active preset
    /// after reattaching to a server that outlived a router restart.
    /// </summary>
    private volatile string? _apiKey;
    private volatile bool _apiKeyResolved;

    public StrataProvider(ILogger<StrataProvider> logger, IServiceScopeFactory scopeFactory)
        : base(logger, scopeFactory)
    {
        _logger = logger;
    }

    // Strata's engine waits up to 15 minutes for its own READY by default (STRATA_ENGINE_READY_S),
    // reading tens of GB of experts from disk — give the whole start a bit more than that.
    protected override TimeSpan StartupTimeout => TimeSpan.FromMinutes(20);

    protected override ServerLaunchSpec BuildLaunchSpec(ModelPreset preset)
    {
        if (string.IsNullOrEmpty(InstallFolderPath))
            throw new InvalidOperationException("Strata folder is not set — configure the server's Strata folder.");

        string python = StrataLayout.PythonPath(InstallFolderPath);
        if (!File.Exists(python))
            throw new FileNotFoundException($"Strata's Python environment was not found at: {python}. Run Strata's {StrataLayout.FirstTimeSetupCommand} in '{InstallFolderPath}' first.");

        string script = Path.Combine(InstallFolderPath, StrataLayout.ServerScriptRelativePath);
        if (!File.Exists(script))
            throw new FileNotFoundException($"Strata server script not found at: {script}");

        string configPath = ResolveRunConfigPath(preset.ModelPath);
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Strata run config not found at: {configPath}. Point the preset's model path at a strata-<model>.json written by Strata's setup.");

        _apiKey = ReadApiKey(configPath);
        _apiKeyResolved = true;

        var args = new List<string>
        {
            "-u", // unbuffered, so the server's output reaches the wrapper (and the logs) as it's written
            script,
            "--engine", "strata",
            "--config", configPath,
            "--port", Port.ToString(CultureInfo.InvariantCulture),
            // Only the router talks to this server; a run config set up for LAN access ("host": "0.0.0.0")
            // shouldn't expose it past the router's own auth.
            "--host", "127.0.0.1",
        };

        if (preset.MainGpu is int gpu)
        {
            args.Add("--gpu");
            args.Add(gpu.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(preset.SlotSavePath))
        {
            args.Add("--slot-save-path");
            args.Add(preset.SlotSavePath);
        }

        // The working directory matters: Strata resolves its engine, tokenizer and data paths from the checkout.
        return new ServerLaunchSpec(python, args, InstallFolderPath);
    }

    // Strata prints "ready: http://127.0.0.1:<port>/v1  (OpenAI: ...)" once the HTTP server is up,
    // which is after the engine has reported READY (i.e. the model is loaded) unless the run
    // config asks for lazy loading — in which case the first request loads it.
    protected override bool IsModelLoadedLine(string line) => ReadyLineRegex().IsMatch(line);

    protected override int? TryParseListeningPort(string line)
    {
        var match = ReadyLineRegex().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    protected override void ApplyRequestHeaders(HttpRequestMessage request)
    {
        if (!_apiKeyResolved)
            ResolveApiKeyFromActivePreset();

        if (_apiKey is { } key)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>
    /// A relative run-config path is taken relative to the Strata folder, where Strata's setup
    /// writes them — so a preset can just say <c>strata-coder.json</c>.
    /// </summary>
    private string ResolveRunConfigPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new InvalidOperationException("The preset has no model path — set it to a Strata run config (strata-<model>.json).");

        return Path.IsPathRooted(modelPath) || string.IsNullOrEmpty(InstallFolderPath)
            ? modelPath
            : Path.GetFullPath(Path.Combine(InstallFolderPath, modelPath));
    }

    /// <summary>
    /// The key the server will require, mirroring Strata's own precedence: <c>--api-key</c> (never
    /// passed by us) or <c>STRATA_API_KEY</c>, else the run config's <c>api_key</c> — a string of
    /// comma-separated keys or a list. Any one of them is accepted, so the first is used.
    /// </summary>
    private string? ReadApiKey(string configPath)
    {
        string? fromEnvironment = FirstKey(Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable));
        if (fromEnvironment is not null)
            return fromEnvironment;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("api_key", out var key))
                return null;

            return key.ValueKind switch
            {
                JsonValueKind.String => FirstKey(key.GetString()),
                JsonValueKind.Array => key.EnumerateArray()
                    .Where(k => k.ValueKind == JsonValueKind.String)
                    .Select(k => FirstKey(k.GetString()))
                    .FirstOrDefault(k => k is not null),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read api_key from Strata run config {ConfigPath}", configPath);
            return null;
        }
    }

    private static string? FirstKey(string? keys) =>
        keys?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    /// <summary>
    /// After reattaching to a server started by a previous router process, no launch was built in
    /// this one — look the run config up from the server's active preset instead.
    /// </summary>
    private void ResolveApiKeyFromActivePreset()
    {
        _apiKeyResolved = true;

        if (ServerInstance?.ActivePresetId is not Guid presetId)
            return;

        try
        {
            using var scope = ScopeFactory.CreateScope();
            var preset = scope.ServiceProvider.GetRequiredService<IPresetManager>().GetById(presetId);
            if (preset is null)
                return;

            string configPath = ResolveRunConfigPath(preset.ModelPath);
            if (File.Exists(configPath))
                _apiKey = ReadApiKey(configPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve the Strata API key for server {ServerId}", ServerInstance?.Id);
        }
    }

    [GeneratedRegex(@"^ready: http://[^\s/]+:(\d+)/v1\b")]
    private static partial Regex ReadyLineRegex();
}
