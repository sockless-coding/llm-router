using System.ComponentModel.DataAnnotations;

using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetCreateModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;
    private readonly IModelLibrary _modelLibrary;

    [BindProperty]
    public PresetViewModel ViewModel { get; set; } = new();

    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();
    public IReadOnlyList<Core.Models.LocalModel> Models { get; set; } = new List<Core.Models.LocalModel>();

    public PresetCreateModel(IPresetManager presetManager, IServerManager serverManager, IModelLibrary modelLibrary)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
        _modelLibrary = modelLibrary;
    }

    public async Task OnGetAsync()
    {
        Servers = _serverManager.GetAllInstances();
        Models = await _modelLibrary.GetAllAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ViewModel.ModelId.HasValue && string.IsNullOrWhiteSpace(ViewModel.ModelPath))
            ModelState.AddModelError("ViewModel.ModelPath", "Select a model from the library, or enter a path manually.");

        if (!ModelState.IsValid)
        {
            Servers = _serverManager.GetAllInstances();
            Models = await _modelLibrary.GetAllAsync();
            return Page();
        }

        // A filled-in manual override always wins over a selected registry model.
        var modelId = string.IsNullOrWhiteSpace(ViewModel.ModelPath) ? ViewModel.ModelId : null;

        var preset = new Core.Models.ModelPreset
        {
            Id = Guid.NewGuid(),
            ServerInstanceId = ViewModel.ServerInstanceId,
            Name = ViewModel.Name,
            ModelId = modelId,
            ModelPath = ViewModel.ModelPath ?? "",
            ContextSize = ViewModel.ContextSize,
            GpuLayers = ViewModel.GpuLayers,
            CacheTypeK = ViewModel.CacheTypeK,
            CacheTypeV = ViewModel.CacheTypeV,
            FlashAttention = ViewModel.FlashAttention,
            Jinja = ViewModel.Jinja,
            SpecType = ViewModel.SpecType,
            Temperature = ViewModel.Temperature,
            TopK = ViewModel.TopK,
            MinP = ViewModel.MinP,
            TopP = ViewModel.TopP,
            RepeatPenalty = ViewModel.RepeatPenalty,
            PresencePenalty = ViewModel.PresencePenalty,
            Threads = ViewModel.Threads,
            ThreadsBatch = ViewModel.ThreadsBatch,
            PredictN = ViewModel.PredictN,
            BatchSize = ViewModel.BatchSize,
            UbatchSize = ViewModel.UbatchSize,
            KeepN = ViewModel.KeepN,
            Seed = ViewModel.Seed,
            IgnoreEos = ViewModel.IgnoreEos,
            Device = ViewModel.Device,
            SplitMode = ViewModel.SplitMode,
            TensorSplit = ViewModel.TensorSplit,
            MainGpu = ViewModel.MainGpu,
            Fit = ViewModel.Fit,
            KvOffload = ViewModel.KvOffload,
            Repack = ViewModel.Repack,
            LoadMode = ViewModel.LoadMode,
            CacheRam = ViewModel.CacheRam,
            RopeScalingType = ViewModel.RopeScalingType,
            RopeScale = ViewModel.RopeScale,
            RopeFreqBase = ViewModel.RopeFreqBase,
            RopeFreqScale = ViewModel.RopeFreqScale,
            YarnOrigCtx = ViewModel.YarnOrigCtx,
            YarnExtFactor = ViewModel.YarnExtFactor,
            YarnAttnFactor = ViewModel.YarnAttnFactor,
            YarnBetaSlow = ViewModel.YarnBetaSlow,
            YarnBetaFast = ViewModel.YarnBetaFast,
            TopNSigma = ViewModel.TopNSigma,
            XtcProbability = ViewModel.XtcProbability,
            XtcThreshold = ViewModel.XtcThreshold,
            TypicalP = ViewModel.TypicalP,
            RepeatLastN = ViewModel.RepeatLastN,
            FrequencyPenalty = ViewModel.FrequencyPenalty,
            DryMultiplier = ViewModel.DryMultiplier,
            DryBase = ViewModel.DryBase,
            DryAllowedLength = ViewModel.DryAllowedLength,
            DryPenaltyLastN = ViewModel.DryPenaltyLastN,
            Mirostat = ViewModel.Mirostat,
            MirostatTau = ViewModel.MirostatTau,
            MirostatEta = ViewModel.MirostatEta,
            DynatempRange = ViewModel.DynatempRange,
            DynatempExp = ViewModel.DynatempExp,
            SpecDraftModel = ViewModel.SpecDraftModel,
            SpecDraftNMax = ViewModel.SpecDraftNMax,
            SpecDraftNMin = ViewModel.SpecDraftNMin,
            SpecDraftPMin = ViewModel.SpecDraftPMin,
            SpecDraftTypeK = ViewModel.SpecDraftTypeK,
            SpecDraftTypeV = ViewModel.SpecDraftTypeV,
            SpecDraftGpuLayers = ViewModel.SpecDraftGpuLayers,
            SpecDraftThreads = ViewModel.SpecDraftThreads,
            Host = ViewModel.Host,
            Port = ViewModel.Port,
            Parallel = ViewModel.Parallel,
            ContBatching = ViewModel.ContBatching,
            Timeout = ViewModel.Timeout,
            ApiKey = ViewModel.ApiKey,
            CachePrompt = ViewModel.CachePrompt,
            Reasoning = ViewModel.Reasoning,
            ReasoningBudget = ViewModel.ReasoningBudget,
            ReasoningFormat = ViewModel.ReasoningFormat,
            Mmproj = ViewModel.Mmproj,
            ImageMinTokens = ViewModel.ImageMinTokens,
            ImageMaxTokens = ViewModel.ImageMaxTokens,
            Lora = ViewModel.Lora,
            ChatTemplate = ViewModel.ChatTemplate,
            ChatTemplateKwargs = ViewModel.ChatTemplateKwargs,
        };

        await _presetManager.CreateAsync(preset);
        return RedirectToPage("Index");
    }
}

public class PresetViewModel
{
    public Guid ServerInstanceId { get; set; }

    [Required, MaxLength(256)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Selected model from the registry. When set, this takes precedence over
    /// <see cref="ModelPath"/> — see <c>PresetManager.ApplyLinkedModelAsync</c>.
    /// </summary>
    public Guid? ModelId { get; set; }

    /// <summary>
    /// Manual path override, used when no registry model is selected. Nullable (not [Required])
    /// because validity depends on ModelId — enforced explicitly in the page handlers. Must stay
    /// nullable: with Nullable enabled project-wide, a non-nullable string here would make the
    /// tag helpers render an implicit "required" attribute on the input, blocking submission
    /// whenever a model is picked from the dropdown instead of typed manually.
    /// </summary>
    [MaxLength(1024)]
    public string? ModelPath { get; set; }

    // Core
    public int? ContextSize { get; set; }
    public int? GpuLayers { get; set; }
    public string? CacheTypeK { get; set; }
    public string? CacheTypeV { get; set; }
    public string? FlashAttention { get; set; }
    public bool? Jinja { get; set; }
    public string? SpecType { get; set; }

    // Sampling (core)
    public float? Temperature { get; set; }
    public int? TopK { get; set; }
    public float? MinP { get; set; }
    public float? TopP { get; set; }
    public float? RepeatPenalty { get; set; }
    public float? PresencePenalty { get; set; }

    // Advanced: Generation
    public int? Threads { get; set; }
    public int? ThreadsBatch { get; set; }
    public int? PredictN { get; set; }
    public int? BatchSize { get; set; }
    public int? UbatchSize { get; set; }
    public int? KeepN { get; set; }
    public long? Seed { get; set; }
    public bool? IgnoreEos { get; set; }

    // Advanced: GPU/Device
    public string? Device { get; set; }
    public string? SplitMode { get; set; }
    public string? TensorSplit { get; set; }
    public int? MainGpu { get; set; }
    public string? Fit { get; set; }
    public bool? KvOffload { get; set; }
    public bool? Repack { get; set; }

    // Advanced: Memory
    public string? LoadMode { get; set; }
    public int? CacheRam { get; set; }

    // Advanced: RoPE Scaling
    public string? RopeScalingType { get; set; }
    public float? RopeScale { get; set; }
    public float? RopeFreqBase { get; set; }
    public float? RopeFreqScale { get; set; }
    public int? YarnOrigCtx { get; set; }
    public float? YarnExtFactor { get; set; }
    public float? YarnAttnFactor { get; set; }
    public float? YarnBetaSlow { get; set; }
    public float? YarnBetaFast { get; set; }

    // Advanced: Sampling (Extended)
    public float? TopNSigma { get; set; }
    public float? XtcProbability { get; set; }
    public float? XtcThreshold { get; set; }
    public float? TypicalP { get; set; }
    public int? RepeatLastN { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? DryMultiplier { get; set; }
    public float? DryBase { get; set; }
    public int? DryAllowedLength { get; set; }
    public int? DryPenaltyLastN { get; set; }
    public int? Mirostat { get; set; }
    public float? MirostatTau { get; set; }
    public float? MirostatEta { get; set; }
    public float? DynatempRange { get; set; }
    public float? DynatempExp { get; set; }

    // Advanced: Speculative Decoding
    public string? SpecDraftModel { get; set; }
    public int? SpecDraftNMax { get; set; }
    public int? SpecDraftNMin { get; set; }
    public float? SpecDraftPMin { get; set; }
    public string? SpecDraftTypeK { get; set; }
    public string? SpecDraftTypeV { get; set; }
    public int? SpecDraftGpuLayers { get; set; }
    public int? SpecDraftThreads { get; set; }

    // Advanced: Server
    public string? Host { get; set; }
    public int? Port { get; set; }
    public int? Parallel { get; set; }
    public bool? ContBatching { get; set; }
    public int? Timeout { get; set; }
    public string? ApiKey { get; set; }
    public bool? CachePrompt { get; set; }

    // Advanced: Reasoning
    public string? Reasoning { get; set; }
    public int? ReasoningBudget { get; set; }
    public string? ReasoningFormat { get; set; }

    // Advanced: Multimodal
    public string? Mmproj { get; set; }
    public int? ImageMinTokens { get; set; }
    public int? ImageMaxTokens { get; set; }

    // Advanced: LoRA
    public string? Lora { get; set; }

    // Advanced: Chat Template
    public string? ChatTemplate { get; set; }
    public string? ChatTemplateKwargs { get; set; }
}

