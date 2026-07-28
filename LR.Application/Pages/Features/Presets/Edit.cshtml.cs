using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetEditModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;

    [BindProperty]
    public PresetViewModel ViewModel { get; set; } = new();

    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    public PresetEditModel(IPresetManager presetManager, IServerManager serverManager)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
    }

    public async Task<IActionResult> OnGetAsync([FromRoute] Guid Id)
    {
        var preset = await _presetManager.GetByIdAsync(Id);
        if (preset is null)
            return NotFound();

        Servers = _serverManager.GetAllInstances();
        MapToViewModel(preset);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromRoute] Guid Id)
    {
        if (!ModelState.IsValid)
        {
            Servers = _serverManager.GetAllInstances();
            return Page();
        }

        var existing = await _presetManager.GetByIdAsync(Id);
        if (existing is null)
            return NotFound();

        // Map ViewModel → Entity directly on the tracked entity
        MapViewModelToEntity(existing);

        // PresetManager's UpdateAsync already copies all new fields + saves
        // We call it with the same entity to trigger EF tracking
        await _presetManager.UpdateAsync(Id, existing);

        return RedirectToPage("Index");
    }

    private void MapToViewModel(Core.Models.ModelPreset preset)
    {
        ViewModel.ServerInstanceId = preset.ServerInstanceId;
        ViewModel.Name = preset.Name;
        ViewModel.ModelPath = preset.ModelPath;
        ViewModel.ContextSize = preset.ContextSize;
        ViewModel.GpuLayers = preset.GpuLayers;
        ViewModel.CacheTypeK = preset.CacheTypeK;
        ViewModel.CacheTypeV = preset.CacheTypeV;
        ViewModel.FlashAttention = preset.FlashAttention;
        ViewModel.Jinja = preset.Jinja;
        ViewModel.SpecType = preset.SpecType;
        ViewModel.Temperature = preset.Temperature;
        ViewModel.TopK = preset.TopK;
        ViewModel.MinP = preset.MinP;
        ViewModel.TopP = preset.TopP;
        ViewModel.RepeatPenalty = preset.RepeatPenalty;
        ViewModel.PresencePenalty = preset.PresencePenalty;
        ViewModel.Threads = preset.Threads;
        ViewModel.ThreadsBatch = preset.ThreadsBatch;
        ViewModel.PredictN = preset.PredictN;
        ViewModel.BatchSize = preset.BatchSize;
        ViewModel.UbatchSize = preset.UbatchSize;
        ViewModel.KeepN = preset.KeepN;
        ViewModel.Seed = preset.Seed;
        ViewModel.IgnoreEos = preset.IgnoreEos;
        ViewModel.Device = preset.Device;
        ViewModel.SplitMode = preset.SplitMode;
        ViewModel.TensorSplit = preset.TensorSplit;
        ViewModel.MainGpu = preset.MainGpu;
        ViewModel.Fit = preset.Fit;
        ViewModel.KvOffload = preset.KvOffload;
        ViewModel.Repack = preset.Repack;
        ViewModel.LoadMode = preset.LoadMode;
        ViewModel.CacheRam = preset.CacheRam;
        ViewModel.RopeScalingType = preset.RopeScalingType;
        ViewModel.RopeScale = preset.RopeScale;
        ViewModel.RopeFreqBase = preset.RopeFreqBase;
        ViewModel.RopeFreqScale = preset.RopeFreqScale;
        ViewModel.YarnOrigCtx = preset.YarnOrigCtx;
        ViewModel.YarnExtFactor = preset.YarnExtFactor;
        ViewModel.YarnAttnFactor = preset.YarnAttnFactor;
        ViewModel.YarnBetaSlow = preset.YarnBetaSlow;
        ViewModel.YarnBetaFast = preset.YarnBetaFast;
        ViewModel.TopNSigma = preset.TopNSigma;
        ViewModel.XtcProbability = preset.XtcProbability;
        ViewModel.XtcThreshold = preset.XtcThreshold;
        ViewModel.TypicalP = preset.TypicalP;
        ViewModel.RepeatLastN = preset.RepeatLastN;
        ViewModel.FrequencyPenalty = preset.FrequencyPenalty;
        ViewModel.DryMultiplier = preset.DryMultiplier;
        ViewModel.DryBase = preset.DryBase;
        ViewModel.DryAllowedLength = preset.DryAllowedLength;
        ViewModel.DryPenaltyLastN = preset.DryPenaltyLastN;
        ViewModel.Mirostat = preset.Mirostat;
        ViewModel.MirostatTau = preset.MirostatTau;
        ViewModel.MirostatEta = preset.MirostatEta;
        ViewModel.DynatempRange = preset.DynatempRange;
        ViewModel.DynatempExp = preset.DynatempExp;
        ViewModel.SpecDraftModel = preset.SpecDraftModel;
        ViewModel.SpecDraftNMax = preset.SpecDraftNMax;
        ViewModel.SpecDraftNMin = preset.SpecDraftNMin;
        ViewModel.SpecDraftPMin = preset.SpecDraftPMin;
        ViewModel.SpecDraftTypeK = preset.SpecDraftTypeK;
        ViewModel.SpecDraftTypeV = preset.SpecDraftTypeV;
        ViewModel.SpecDraftGpuLayers = preset.SpecDraftGpuLayers;
        ViewModel.SpecDraftThreads = preset.SpecDraftThreads;
        ViewModel.Host = preset.Host;
        ViewModel.Port = preset.Port;
        ViewModel.Parallel = preset.Parallel;
        ViewModel.ContBatching = preset.ContBatching;
        ViewModel.Timeout = preset.Timeout;
        ViewModel.ApiKey = preset.ApiKey;
        ViewModel.CachePrompt = preset.CachePrompt;
        ViewModel.Reasoning = preset.Reasoning;
        ViewModel.ReasoningBudget = preset.ReasoningBudget;
        ViewModel.Mmproj = preset.Mmproj;
        ViewModel.ImageMinTokens = preset.ImageMinTokens;
        ViewModel.ImageMaxTokens = preset.ImageMaxTokens;
        ViewModel.Lora = preset.Lora;
        ViewModel.ChatTemplate = preset.ChatTemplate;
    }

    private void MapViewModelToEntity(Core.Models.ModelPreset entity)
    {
        entity.Name = ViewModel.Name;
        entity.ModelPath = ViewModel.ModelPath;
        entity.ContextSize = ViewModel.ContextSize;
        entity.GpuLayers = ViewModel.GpuLayers;
        entity.CacheTypeK = ViewModel.CacheTypeK;
        entity.CacheTypeV = ViewModel.CacheTypeV;
        entity.FlashAttention = ViewModel.FlashAttention;
        entity.Jinja = ViewModel.Jinja;
        entity.SpecType = ViewModel.SpecType;
        entity.Temperature = ViewModel.Temperature;
        entity.TopK = ViewModel.TopK;
        entity.MinP = ViewModel.MinP;
        entity.TopP = ViewModel.TopP;
        entity.RepeatPenalty = ViewModel.RepeatPenalty;
        entity.PresencePenalty = ViewModel.PresencePenalty;
        entity.Threads = ViewModel.Threads;
        entity.ThreadsBatch = ViewModel.ThreadsBatch;
        entity.PredictN = ViewModel.PredictN;
        entity.BatchSize = ViewModel.BatchSize;
        entity.UbatchSize = ViewModel.UbatchSize;
        entity.KeepN = ViewModel.KeepN;
        entity.Seed = ViewModel.Seed;
        entity.IgnoreEos = ViewModel.IgnoreEos;
        entity.Device = ViewModel.Device;
        entity.SplitMode = ViewModel.SplitMode;
        entity.TensorSplit = ViewModel.TensorSplit;
        entity.MainGpu = ViewModel.MainGpu;
        entity.Fit = ViewModel.Fit;
        entity.KvOffload = ViewModel.KvOffload;
        entity.Repack = ViewModel.Repack;
        entity.LoadMode = ViewModel.LoadMode;
        entity.CacheRam = ViewModel.CacheRam;
        entity.RopeScalingType = ViewModel.RopeScalingType;
        entity.RopeScale = ViewModel.RopeScale;
        entity.RopeFreqBase = ViewModel.RopeFreqBase;
        entity.RopeFreqScale = ViewModel.RopeFreqScale;
        entity.YarnOrigCtx = ViewModel.YarnOrigCtx;
        entity.YarnExtFactor = ViewModel.YarnExtFactor;
        entity.YarnAttnFactor = ViewModel.YarnAttnFactor;
        entity.YarnBetaSlow = ViewModel.YarnBetaSlow;
        entity.YarnBetaFast = ViewModel.YarnBetaFast;
        entity.TopNSigma = ViewModel.TopNSigma;
        entity.XtcProbability = ViewModel.XtcProbability;
        entity.XtcThreshold = ViewModel.XtcThreshold;
        entity.TypicalP = ViewModel.TypicalP;
        entity.RepeatLastN = ViewModel.RepeatLastN;
        entity.FrequencyPenalty = ViewModel.FrequencyPenalty;
        entity.DryMultiplier = ViewModel.DryMultiplier;
        entity.DryBase = ViewModel.DryBase;
        entity.DryAllowedLength = ViewModel.DryAllowedLength;
        entity.DryPenaltyLastN = ViewModel.DryPenaltyLastN;
        entity.Mirostat = ViewModel.Mirostat;
        entity.MirostatTau = ViewModel.MirostatTau;
        entity.MirostatEta = ViewModel.MirostatEta;
        entity.DynatempRange = ViewModel.DynatempRange;
        entity.DynatempExp = ViewModel.DynatempExp;
        entity.SpecDraftModel = ViewModel.SpecDraftModel;
        entity.SpecDraftNMax = ViewModel.SpecDraftNMax;
        entity.SpecDraftNMin = ViewModel.SpecDraftNMin;
        entity.SpecDraftPMin = ViewModel.SpecDraftPMin;
        entity.SpecDraftTypeK = ViewModel.SpecDraftTypeK;
        entity.SpecDraftTypeV = ViewModel.SpecDraftTypeV;
        entity.SpecDraftGpuLayers = ViewModel.SpecDraftGpuLayers;
        entity.SpecDraftThreads = ViewModel.SpecDraftThreads;
        entity.Host = ViewModel.Host;
        entity.Port = ViewModel.Port;
        entity.Parallel = ViewModel.Parallel;
        entity.ContBatching = ViewModel.ContBatching;
        entity.Timeout = ViewModel.Timeout;
        entity.ApiKey = ViewModel.ApiKey;
        entity.CachePrompt = ViewModel.CachePrompt;
        entity.Reasoning = ViewModel.Reasoning;
        entity.ReasoningBudget = ViewModel.ReasoningBudget;
        entity.Mmproj = ViewModel.Mmproj;
        entity.ImageMinTokens = ViewModel.ImageMinTokens;
        entity.ImageMaxTokens = ViewModel.ImageMaxTokens;
        entity.Lora = ViewModel.Lora;
        entity.ChatTemplate = ViewModel.ChatTemplate;
    }
}