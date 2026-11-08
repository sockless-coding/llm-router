using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;
using LR.Providers.LlamaCpp;

namespace LR.Application.Pages.Features.Presets;

/// <summary>
/// Builds a preview of the llama.cpp command line a <see cref="PresetViewModel"/> would launch
/// with, without persisting anything — backs the live "generated command" panel on the
/// create/edit form. Mirrors the registry-link resolution <see cref="LR.Core.Services.PresetManager"/>
/// does on save, but reads the model library directly instead of writing the preset back to it.
/// </summary>
public static class PresetPreview
{
    /// <summary>
    /// Resolves <see cref="ModelPreset.ModelId"/>, <see cref="ModelPreset.SpecDraftModelId"/> and
    /// <see cref="ModelPreset.MmprojId"/> against the model registry, copying each linked model's
    /// file path onto the corresponding path field. No-ops per-field when unset or unresolvable.
    /// </summary>
    public static async Task ResolveLinkedModelsAsync(ModelPreset preset, IModelLibrary modelLibrary)
    {
        if (preset.ModelId.HasValue)
        {
            var model = await modelLibrary.GetByIdAsync(preset.ModelId.Value);
            if (model != null)
                preset.ModelPath = model.FilePath;
        }

        if (preset.SpecDraftModelId.HasValue)
        {
            var model = await modelLibrary.GetByIdAsync(preset.SpecDraftModelId.Value);
            if (model != null)
                preset.SpecDraftModel = model.FilePath;
        }

        if (preset.MmprojId.HasValue)
        {
            var model = await modelLibrary.GetByIdAsync(preset.MmprojId.Value);
            if (model != null)
                preset.Mmproj = model.FilePath;
        }
    }

    /// <summary>
    /// A user-facing error when a linked library model (main, draft or projector) lives on a
    /// different machine than the preset's server — this router vs. a remote agent, or two agents —
    /// since its path wouldn't exist where the server runs. Null when everything matches.
    /// </summary>
    public static async Task<string?> FindMachineMismatchAsync(PresetViewModel viewModel, IServerManager serverManager, IModelLibrary modelLibrary)
    {
        var server = serverManager.GetAllInstances().FirstOrDefault(s => s.Id == viewModel.ServerInstanceId);
        if (server is null) return null;

        foreach (var (id, role) in new[] { (viewModel.ModelId, "model"), (viewModel.SpecDraftModelId, "draft model"), (viewModel.MmprojId, "projector") })
        {
            if (id is not { } modelId) continue;
            if (await modelLibrary.GetByIdAsync(modelId) is { } model && model.AgentId != server.AgentId)
                return $"The selected {role} '{model.Name}' is on a different machine than server '{server.Name}'. Pick one from the same machine, or copy it there from the Agents page.";
        }
        return null;
    }

    /// <summary>The generated command line, plus the raw argument list it was built from.</summary>
    public sealed record Result(string CommandLine, IReadOnlyList<string> Args);

    public static Result Build(ModelPreset preset, ServerEngine engine = ServerEngine.LlamaCpp)
    {
        if (string.IsNullOrWhiteSpace(preset.ModelPath))
            preset.ModelPath = "<no model selected>";

        if (engine == ServerEngine.Strata)
            return BuildStrata(preset);

        if (engine == ServerEngine.StableDiffusionCpp)
            return BuildStableDiffusion(preset);

        if (engine == ServerEngine.Vllm)
            return BuildVllm(preset);

        var args = new LlamaCppArgBuilder { Port = 8080 }.Build(preset);
        var commandLine = "llama-server " + string.Join(' ', args.Select(QuoteIfNeeded));

        return new Result(commandLine, args);
    }

    /// <summary>
    /// A Strata preset: what its first start runs to prepare the model (Strata's setup in its
    /// existing-GGUF mode; the family/size and the engine build are filled in at start), then the
    /// server launch from the prepared run config.
    /// </summary>
    private static Result BuildStrata(ModelPreset preset)
    {
        if (preset.ModelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var own = $"python serve/server.py --engine strata --config {QuoteIfNeeded(preset.ModelPath)} --host 127.0.0.1 --port <port>";
            return new Result(own, Array.Empty<string>());
        }

        List<string> setupArgs;
        try
        {
            setupArgs = StrataPresetSettings.SetupArgs(preset, engine: null);
        }
        catch (InvalidOperationException ex)
        {
            return new Result(ex.Message, Array.Empty<string>());
        }

        var dir = Path.GetDirectoryName(preset.ModelPath) ?? preset.ModelPath;
        var commandLine =
            "# first start (and after a change): prepare the model\n" +
            $"python setup.py --gguf-dir {QuoteIfNeeded(dir)} --family <from the file> --model <from the file> " +
            string.Join(' ', setupArgs.Select(QuoteIfNeeded)) + "\n" +
            "# every start\n" +
            "python serve/server.py --engine strata --config <prepared config> --host 127.0.0.1 --port <port>";
        return new Result(commandLine, setupArgs);
    }

    /// <summary>A vLLM preset: its environment, then the <c>vllm serve</c> launch (the port is assigned at start).</summary>
    private static Result BuildVllm(ModelPreset preset)
    {
        List<string> args;
        Dictionary<string, string> environment;
        try
        {
            args = VllmPresetSettings.ServeArgs(preset);
            environment = VllmPresetSettings.EnvironmentOf(preset);
        }
        catch (InvalidOperationException ex)
        {
            return new Result(ex.Message, Array.Empty<string>());
        }

        args.AddRange(["--host", "127.0.0.1", "--port", "<port>"]);
        var exports = string.Concat(environment.Select(e => $"{e.Key}={QuoteIfNeeded(e.Value)} "));
        return new Result(exports + "vllm " + string.Join(' ', args.Select(QuoteIfNeeded)), args);
    }

    /// <summary>A stable-diffusion.cpp preset: the <c>sd-server</c> launch (the port is assigned at start).</summary>
    private static Result BuildStableDiffusion(ModelPreset preset)
    {
        List<string> args;
        try
        {
            args = StableDiffusionPresetSettings.ServerArgs(preset);
        }
        catch (InvalidOperationException ex)
        {
            return new Result(ex.Message, Array.Empty<string>());
        }

        args.AddRange(["--listen-ip", "127.0.0.1", "--listen-port", "<port>"]);
        return new Result("sd-server " + string.Join(' ', args.Select(QuoteIfNeeded)), args);
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        var needsQuoting = arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '`' or '\\');
        if (!needsQuoting)
            return arg;

        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
