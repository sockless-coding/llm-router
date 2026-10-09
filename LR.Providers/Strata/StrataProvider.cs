using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.Strata;

/// <summary>
/// Runs Strata (github.com/Niko1221/Strata): a Python HTTP frontend (<c>serve/server.py</c>) that
/// drives Strata's native engine and serves OpenAI-compatible <c>/v1/chat/completions</c>,
/// Anthropic-compatible <c>/v1/messages</c>, and llama.cpp-style <c>/health</c>, <c>/props</c>
/// and <c>/slots</c> — so request handling, health and capacity reporting are all inherited.
///
/// Strata starts from a run config (<c>strata-&lt;model&gt;.json</c>: the engine, its tuned arguments,
/// the prepared model files). A preset's <see cref="ModelPreset.ModelPath"/> is normally a GGUF from the
/// model library (a Qwen3.8-Flash-Next GSQ-RCO / Swift / Coder / Unsloth file); the first time the
/// preset starts — and again whenever its model, its Strata settings
/// (<see cref="StrataPresetSettings"/>) or the install change — <see cref="PrepareAsync"/> runs
/// Strata's own <c>setup.py</c> on it in its existing-GGUF mode (<c>--gguf-dir</c>), which builds the
/// model's pack and the MTP draft layer and writes a run config tuned to this PC; the router keeps a
/// copy per preset. A <see cref="ModelPreset.ModelPath"/> that is itself a run config (one made by
/// Strata's <c>START-HERE.bat</c>) is used as it is. The server is launched the way Strata's own
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

        bool ownRunConfig = IsRunConfig(preset.ModelPath);
        string configPath = RunConfigPathFor(preset);
        if (ownRunConfig && !File.Exists(configPath))
            throw new FileNotFoundException($"Strata run config not found at: {configPath}.");

        _apiKey = File.Exists(configPath) ? ReadApiKey(configPath) : null;
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

        // A prepared config already has the GPU the preset chose (strata.gpu); a run config of
        // Strata's own takes the preset's Main GPU, as before.
        if (ownRunConfig && preset.MainGpu is int gpu)
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

    /// <summary>True if the preset points at a run config of Strata's own rather than at a GGUF.</summary>
    private static bool IsRunConfig(string? modelPath) =>
        modelPath is not null && modelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The run config <paramref name="preset"/> starts from: its own, or the one prepared for it.</summary>
    private string RunConfigPathFor(ModelPreset preset) =>
        IsRunConfig(preset.ModelPath) ? ResolveRunConfigPath(preset.ModelPath) : PreparedConfigPath(preset.Id);

    /// <summary>Where the router keeps the run config prepared for a preset, inside the install it was prepared with.</summary>
    private string PreparedConfigPath(Guid presetId) =>
        Path.Combine(InstallFolderPath ?? throw new InvalidOperationException("Strata folder is not set."),
            ".router", "presets", $"{presetId:N}.json");

    /// <summary>
    /// A relative run-config path is taken relative to the Strata folder, where Strata's setup
    /// writes them — so a preset can just say <c>strata-coder.json</c>.
    /// </summary>
    private string ResolveRunConfigPath(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new InvalidOperationException("The preset has no model — pick a Qwen3.8-Flash-Next GGUF from the model library.");

        return Path.IsPathRooted(modelPath) || string.IsNullOrEmpty(InstallFolderPath)
            ? modelPath
            : Path.GetFullPath(Path.Combine(InstallFolderPath, modelPath));
    }

    /// <summary>One preparation at a time per install: Strata's setup writes into the checkout and its data folder.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PrepareLocks = new(StringComparer.OrdinalIgnoreCase);

    private const string PreparedConfigMarker = "STRATA_PREP_CONFIG ";
    private const string PrepareErrorMarker = "STRATA_PREP_ERROR ";

    /// <summary>
    /// Finds the Strata family and size of the GGUF in <c>argv[1]</c> by matching its file name against
    /// setup's own <c>FAMILIES</c>/<c>MODELS</c> file patterns, then runs <c>setup.py</c> in its
    /// existing-GGUF mode with the rest of <c>argv</c>, and prints the run config it wrote.
    /// </summary>
    private const string PrepareDriver =
        "import os, sys\n" +
        "gguf, extra = os.path.abspath(sys.argv[1]), sys.argv[2:]\n" +
        "sys.path.insert(0, '.')\n" +
        "import setup as s\n" +
        "name, found = os.path.basename(gguf), None\n" +
        "for fk, f in s.FAMILIES.items():\n" +
        "    for mk, m in s.MODELS.items():\n" +
        "        if fk not in m.get('families', ('qwen', 'swift')):\n" +
        "            continue\n" +
        "        try:\n" +
        "            if (m.get('file') or f.get('file', '')).format(q=mk, i=1) == name:\n" +
        "                found = (fk, mk)\n" +
        "        except (KeyError, IndexError, ValueError):\n" +
        "            pass\n" +
        "if found is None:\n" +
        "    print('" + PrepareErrorMarker + "' + name + ' is not a model Strata runs. ' + getattr(s, 'SUPPORTED_GGUFS', ''), flush=True)\n" +
        "    sys.exit(3)\n" +
        "fam, model = found\n" +
        "print('Strata model: %s %s' % (s.FAMILIES[fam].get('title', fam), model), flush=True)\n" +
        "sys.argv = ['setup.py', '--gguf-dir', os.path.dirname(gguf), '--family', fam, '--model', model] + extra\n" +
        "rc = s.main()\n" +
        "if rc:\n" +
        "    sys.exit(rc)\n" +
        "tag = (s.FAMILIES[fam].get('tag', '') + model).lower()\n" +
        "print('" + PreparedConfigMarker + "' + os.path.join(str(s.ROOT), 'strata-%s.json' % tag), flush=True)\n";

    /// <summary>
    /// Prepares the preset's GGUF for Strata unless the run config prepared before still matches —
    /// same model file, same Strata settings, same install version and engine. Runs Strata's
    /// <c>setup.py</c> (see the class summary): the first time, that installs its remaining Python
    /// packages, builds the model's pack, and downloads the ~5 GB MTP draft layer (shared by every
    /// model), so it can take several minutes; its output goes to the server log and the start progress.
    /// </summary>
    protected override async Task PrepareAsync(ModelPreset preset, Func<StartupProgressEvent, Task>? onProgress, CancellationToken cancellationToken)
    {
        if (IsRunConfig(preset.ModelPath))
            return;

        if (string.IsNullOrEmpty(InstallFolderPath))
            throw new InvalidOperationException("Strata folder is not set — bind the server to a Strata install.");
        var python = StrataLayout.PythonPath(InstallFolderPath);
        if (!File.Exists(python))
            throw new FileNotFoundException($"Strata's Python environment was not found at: {python}. Install a Strata release from the Engines page.");
        if (string.IsNullOrWhiteSpace(preset.ModelPath))
            throw new InvalidOperationException("The preset has no model — pick a Qwen3.8-Flash-Next GGUF from the model library.");

        var gguf = Path.GetFullPath(preset.ModelPath);
        if (!gguf.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !File.Exists(gguf))
            throw new FileNotFoundException($"The preset's model wasn't found: {gguf}");
        if (SplitGguf.AllShards(gguf).FirstOrDefault(s => !File.Exists(s)) is { } missing)
            throw new FileNotFoundException($"A part of the split model is missing: {missing}. Download the model again from the Models page.");

        var setupArgs = StrataPresetSettings.SetupArgs(preset, StrataLayout.InstalledEngine(InstallFolderPath));
        var configPath = PreparedConfigPath(preset.Id);
        var keyPath = configPath + ".key";
        var key = PreparationKey(gguf, setupArgs);
        if (File.Exists(configPath) && File.Exists(keyPath) && File.ReadAllText(keyPath) == key)
            return;

        var gate = PrepareLocks.GetOrAdd(InstallFolderPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            await ReportPreparingAsync(onProgress, "Preparing the model for Strata (first start with this model and settings; can take several minutes)…", 0);
            await LogProviderMessage(ServerLogLevel.Info,
                $"Preparing {Path.GetFileName(gguf)} for Strata: setup.py --gguf-dir {Path.GetDirectoryName(gguf)} {string.Join(' ', setupArgs)}");

            string? written = null, error = null;
            var tail = new Queue<string>();
            double lastReport = 0;
            var result = await ProcessRunner.RunAsync(python, ["-u", "-c", PrepareDriver, gguf, .. setupArgs], InstallFolderPath, null,
                async line =>
                {
                    if (line.StartsWith(PreparedConfigMarker, StringComparison.Ordinal)) { written = line[PreparedConfigMarker.Length..].Trim(); return; }
                    if (line.StartsWith(PrepareErrorMarker, StringComparison.Ordinal)) error = line[PrepareErrorMarker.Length..].Trim();
                    if (string.IsNullOrWhiteSpace(line)) return;

                    tail.Enqueue(line.Trim());
                    if (tail.Count > 8) tail.Dequeue();
                    await LogProviderMessage(ServerLogLevel.Info, line.TrimEnd());
                    if (stopwatch.Elapsed.TotalSeconds - lastReport >= 2)
                    {
                        lastReport = stopwatch.Elapsed.TotalSeconds;
                        await ReportPreparingAsync(onProgress, $"Preparing the model for Strata: {line.Trim()}", lastReport);
                    }
                }, cancellationToken);

            if (result.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            if (result.ExitCode != 0 || written is null || !File.Exists(written))
                throw new InvalidOperationException("Preparing the model for Strata failed: " +
                    (error ?? string.Join(" | ", tail)) + " (the server log has Strata's full output)");

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.Copy(written, configPath, overwrite: true);
            File.WriteAllText(keyPath, key);
            await LogProviderMessage(ServerLogLevel.Info, $"Model prepared for Strata in {stopwatch.Elapsed.TotalSeconds:F0}s: {configPath}");
        }
        finally
        {
            gate.Release();
        }
    }

    private Task ReportPreparingAsync(Func<StartupProgressEvent, Task>? onProgress, string message, double elapsedSeconds) =>
        onProgress?.Invoke(new StartupProgressEvent
        {
            InstanceId = ServerInstance?.Id ?? Guid.Empty,
            EventType = StartupEventType.HealthChecking,
            Message = message,
            ElapsedSeconds = elapsedSeconds,
        }) ?? Task.CompletedTask;

    /// <summary>
    /// What a prepared run config depends on: the model file (path, size, time), the setup arguments
    /// (the preset's Strata settings and the engine build), and the install's source and engine version.
    /// </summary>
    private string PreparationKey(string gguf, IReadOnlyList<string> setupArgs)
    {
        var file = new FileInfo(gguf);
        var material = string.Join("\n",
            gguf, file.Length.ToString(CultureInfo.InvariantCulture), file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            string.Join(' ', setupArgs),
            StrataLayout.ReadSourceVersion(InstallFolderPath!) ?? "",
            StrataLayout.ReadEngineVersion(InstallFolderPath!) ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
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

            string configPath = RunConfigPathFor(preset);
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
