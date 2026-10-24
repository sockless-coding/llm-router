using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.StableDiffusionCpp;

/// <summary>
/// Runs stable-diffusion.cpp's <c>sd-server</c> (github.com/leejet/stable-diffusion.cpp): one model
/// context per process, loaded from the command line before the HTTP server starts listening, serving
/// OpenAI-compatible <c>/v1/images/generations</c> (plus <c>/sdapi/v1/*</c> and its native
/// <c>/sdcpp/v1/*</c>). Launch arguments come from <see cref="StableDiffusionPresetSettings"/>. It has
/// no llama.cpp-style <c>/health</c>, <c>/props</c> or <c>/slots</c>; health is its <c>/v1/models</c>,
/// and it generates one image job at a time, so the router sends it one request at a time. The native
/// async job API (image and video jobs with progress previews) is exposed through
/// <see cref="IGenerationJobProvider"/> for the Playground.
/// </summary>
public partial class StableDiffusionCppProvider : ManagedServerProviderBase, IGenerationJobProvider
{
    public override ServerEngine Engine => ServerEngine.StableDiffusionCpp;

    /// <summary>The OpenAI image-generation path the gateway posts to.</summary>
    public const string ImageGenerationsEndpoint = "/v1/images/generations";

    /// <summary>The <c>sd-server</c> executable name for this OS.</summary>
    public static string ServerExecutableName => SdCppReleaseProfile.ServerExecutable;

    /// <summary>
    /// Request fields outside the OpenAI images API that clients commonly send (A1111/ComfyUI
    /// habits), mapped onto stable-diffusion.cpp's native request schema.
    /// </summary>
    private static readonly (string Field, string[] Path)[] ExtensionFields =
    [
        ("negative_prompt", ["negative_prompt"]),
        ("seed", ["seed"]),
        ("clip_skip", ["clip_skip"]),
        ("steps", ["sample_params", "sample_steps"]),
        ("sample_steps", ["sample_params", "sample_steps"]),
        ("sampler", ["sample_params", "sample_method"]),
        ("sampler_name", ["sample_params", "sample_method"]),
        ("sample_method", ["sample_params", "sample_method"]),
        ("scheduler", ["sample_params", "scheduler"]),
        ("cfg_scale", ["sample_params", "guidance", "txt_cfg"]),
        ("guidance", ["sample_params", "guidance", "distilled_guidance"]),
    ];

    private const string ExtraArgsOpen = "<sd_cpp_extra_args>";
    private const string ExtraArgsClose = "</sd_cpp_extra_args>";

    public StableDiffusionCppProvider(ILogger<StableDiffusionCppProvider> logger, IServiceScopeFactory scopeFactory)
        : base(logger, scopeFactory)
    {
    }

    // A single image on a large model (or on the CPU) can take many minutes.
    protected override TimeSpan RequestTimeout => TimeSpan.FromMinutes(30);

    protected override string HealthCheckPath => "/v1/models";

    protected override bool HasLlamaServerEndpoints => false;

    /// <summary>sd-server runs one generation at a time; more in flight would only queue inside it.</summary>
    public override int? MaxConcurrentRequests => 1;

    protected override ServerLaunchSpec BuildLaunchSpec(ModelPreset preset)
    {
        if (string.IsNullOrEmpty(InstallFolderPath))
            throw new InvalidOperationException("Server executable path is not set — configure the server's stable-diffusion.cpp folder or bind it to a managed build.");

        string executablePath = Path.Combine(InstallFolderPath, ServerExecutableName);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException($"Server executable not found at: {executablePath}");

        var args = StableDiffusionPresetSettings.ServerArgs(preset);
        args.AddRange(["--listen-ip", "127.0.0.1", "--listen-port", Port.ToString(CultureInfo.InvariantCulture)]);
        return new ServerLaunchSpec(executablePath, args, InstallFolderPath);
    }

    // sd-server loads the command-line model synchronously and only then logs
    // "listening on: http://<ip>:<port>" — so that one line means both "loaded" and "listening".
    protected override bool IsModelLoadedLine(string line) => ListeningOnRegex().IsMatch(line);

    protected override int? TryParseListeningPort(string line)
    {
        var match = ListeningOnRegex().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    protected override string PrepareRawRequestPayload(string endpoint, string payload) =>
        endpoint == ImageGenerationsEndpoint ? ToSdCppImageRequest(payload) : payload;

    public async Task<string?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (status, body) = await SendServerRequestAsync(HttpMethod.Get, "/sdcpp/v1/capabilities", null, cancellationToken);
            return IsSuccess(status) ? body : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<string> SubmitJobAsync(GenerationJobKind kind, string payload, CancellationToken cancellationToken = default)
    {
        var path = kind == GenerationJobKind.Video ? "/sdcpp/v1/vid_gen" : "/sdcpp/v1/img_gen";
        var body = await SendJobRequestAsync(HttpMethod.Post, path, payload, cancellationToken);
        return JsonNode.Parse(body)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"sd-server accepted the job but returned no id: {body}");
    }

    public Task<string> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendJobRequestAsync(HttpMethod.Get, $"/sdcpp/v1/jobs/{Uri.EscapeDataString(jobId)}", null, cancellationToken);

    public Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        SendJobRequestAsync(HttpMethod.Post, $"/sdcpp/v1/jobs/{Uri.EscapeDataString(jobId)}/cancel", "{}", cancellationToken);

    /// <summary>
    /// A native-API call whose failure surfaces sd-server's own reason (<c>{"error": "..."}</c>, e.g.
    /// "loaded model does not support vid_gen") rather than the raw response.
    /// </summary>
    private async Task<string> SendJobRequestAsync(HttpMethod method, string path, string? payload, CancellationToken cancellationToken)
    {
        var (status, body) = await SendServerRequestAsync(method, path, payload, cancellationToken);
        if (IsSuccess(status))
            return body;

        await LogProviderMessage(ServerLogLevel.Warning, $"Request to {path} failed: {(int)status} - {body}");
        throw new HttpRequestException(NativeErrorMessage(body) ?? $"sd-server returned {(int)status} for {path}.", null, status);
    }

    /// <summary>The <c>error</c> text of an sd-server error body (a string, or an object with a <c>message</c>).</summary>
    public static string? NativeErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                JsonObject o => o["message"]?.GetValue<string>(),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsSuccess(System.Net.HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>
    /// Turns an OpenAI <c>/v1/images/generations</c> body into what sd-server takes: drops fields it
    /// doesn't understand (<c>model</c> — the loaded model is the preset's; <c>response_format</c> —
    /// it always answers <c>b64_json</c>; <c>quality</c>/<c>style</c>/<c>user</c>), and folds the
    /// extension fields in <see cref="ExtensionFields"/> into an <c>&lt;sd_cpp_extra_args&gt;</c> block
    /// on the prompt, merged with one the client already wrote there. Anything unparseable is passed
    /// through as-is for sd-server to reject.
    /// </summary>
    public static string ToSdCppImageRequest(string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject body)
            return payload;

        foreach (var field in new[] { "model", "response_format", "quality", "style", "user", "stream" })
            body.Remove(field);

        var extra = new JsonObject();
        foreach (var (field, path) in ExtensionFields)
        {
            if (!body.TryGetPropertyValue(field, out var value))
                continue;
            body.Remove(field);
            if (value is null)
                continue;

            var target = extra;
            foreach (var segment in path[..^1])
            {
                if (target[segment] is not JsonObject child)
                    target[segment] = child = new JsonObject();
                target = child;
            }
            target[path[^1]] ??= value.DeepClone();
        }

        if (extra.Count == 0)
            return body.ToJsonString();

        var prompt = body["prompt"]?.GetValue<string>() ?? "";
        int open = prompt.IndexOf(ExtraArgsOpen, StringComparison.Ordinal);
        int close = open < 0 ? -1 : prompt.IndexOf(ExtraArgsClose, open, StringComparison.Ordinal);
        if (open >= 0 && close > open)
        {
            // The client's own block wins over the extension fields where both set something.
            var own = prompt[(open + ExtraArgsOpen.Length)..close];
            if (JsonNode.Parse(own) is JsonObject ownArgs)
                Merge(extra, ownArgs);
            prompt = (prompt[..open] + prompt[(close + ExtraArgsClose.Length)..]).Trim();
        }

        body["prompt"] = $"{prompt} {ExtraArgsOpen}{extra.ToJsonString()}{ExtraArgsClose}";
        return body.ToJsonString();
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is JsonObject sourceChild && target[key] is JsonObject targetChild)
                Merge(targetChild, sourceChild);
            else
                target[key] = value?.DeepClone();
        }
    }

    [GeneratedRegex(@"listening on:?\s+http://[^\s:/]+:(\d+)")]
    private static partial Regex ListeningOnRegex();
}
