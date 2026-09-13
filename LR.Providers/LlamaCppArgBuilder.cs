using System.Globalization;

using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Builds command-line arguments for the llama.cpp server from a ModelPreset.
/// Extracted from LlamaCppProvider to isolate argument construction logic.
/// </summary>
public class LlamaCppArgBuilder
{
    /// <summary>
    /// Port override used when preset.Port is not set (falls back to this value).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Builds the full command-line argument list from a ModelPreset.
    /// Override or extend in derived classes for backend-specific flags.
    /// </summary>
    public List<string> Build(ModelPreset preset)
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
        AddArgIfSet(args, "--fit-target", preset.FitTarget);
        AddIntArg(args, "--fit-ctx", preset.FitCtx);
        if (preset.KvOffload.HasValue)
            args.Add(preset.KvOffload.Value ? "--kv-offload" : "--no-kv-offload");
        if (preset.Repack.HasValue)
            args.Add(preset.Repack.Value ? "--repack" : "--no-repack");
        if (preset.CpuMoe.HasValue && preset.CpuMoe.Value)
            args.Add("--cpu-moe");
        AddIntArg(args, "--n-cpu-moe", preset.NCpuMoe);
        AddArgIfSet(args, "--override-tensor", preset.OverrideTensor);

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
        AddArgIfSet(args, "--samplers", preset.Samplers);
        AddArgIfSet(args, "--sampler-seq", preset.SamplerSeq);
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
        AddRepeatedArg(args, "--dry-sequence-breaker", preset.DrySequenceBreaker);
        AddIntArg(args, "--mirostat", preset.Mirostat);
        AddFloatArg(args, "--mirostat-ent", preset.MirostatTau);
        AddFloatArg(args, "--mirostat-lr", preset.MirostatEta);
        AddFloatArg(args, "--dynatemp-range", preset.DynatempRange);
        AddFloatArg(args, "--dynatemp-exp", preset.DynatempExp);

        // --- Advanced: Speculative Decoding ---
        AddArgIfSet(args, "--spec-draft-model", preset.SpecDraftModel);
        AddArgIfSet(args, "--spec-draft-hf", preset.SpecDraftHfRepo);
        AddArgIfSet(args, "--spec-draft-device", preset.SpecDraftDevice);
        AddIntArg(args, "--spec-draft-n-max", preset.SpecDraftNMax);
        AddIntArg(args, "--spec-draft-n-min", preset.SpecDraftNMin);
        AddFloatArg(args, "--draft-p-min", preset.SpecDraftPMin);
        AddFloatArg(args, "--spec-draft-p-split", preset.SpecDraftPSplit);
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
        AddIntArg(args, "--cache-reuse", preset.CacheReuse);
        if (preset.ContextShift.HasValue)
            args.Add(preset.ContextShift.Value ? "--context-shift" : "--no-context-shift");
        if (preset.KvUnified.HasValue)
            args.Add(preset.KvUnified.Value ? "--kv-unified" : "--no-kv-unified");
        AddFloatArg(args, "--slot-prompt-similarity", preset.SlotPromptSimilarity);
        AddIntArg(args, "--sleep-idle-seconds", preset.SleepIdleSeconds);

        // --- Advanced: Reasoning ---
        AddArgIfSet(args, "--reasoning", preset.Reasoning);
        AddArgIfSet(args, "--reasoning-effort", preset.ReasoningEffort);
        AddIntArg(args, "--reasoning-budget", preset.ReasoningBudget);
        AddArgIfSet(args, "--reasoning-budget-message", preset.ReasoningBudgetMessage);
        AddArgIfSet(args, "--reasoning-format", preset.ReasoningFormat);
        if (preset.ReasoningPreserve.HasValue)
            args.Add(preset.ReasoningPreserve.Value ? "--reasoning-preserve" : "--no-reasoning-preserve");

        // --- Advanced: Multimodal ---
        AddArgIfSet(args, "--mmproj", preset.Mmproj);
        AddArgIfSet(args, "--mmproj-url", preset.MmprojUrl);
        if (preset.MmprojAuto.HasValue)
            args.Add(preset.MmprojAuto.Value ? "--mmproj-auto" : "--no-mmproj-auto");
        if (preset.MmprojOffload.HasValue)
            args.Add(preset.MmprojOffload.Value ? "--mmproj-offload" : "--no-mmproj-offload");
        AddArgIfSet(args, "--mmproj-device", preset.MmprojDevice);
        AddIntArg(args, "--image-min-tokens", preset.ImageMinTokens);
        AddIntArg(args, "--image-max-tokens", preset.ImageMaxTokens);
        AddIntArg(args, "--mtmd-batch-max-tokens", preset.MtmdBatchMaxTokens);
        AddFloatArg(args, "--video-fps", preset.VideoFps);
        AddIntArg(args, "--video-timestamp-interval", preset.VideoTimestampInterval);
        AddArgIfSet(args, "--video-ffmpeg-dir", preset.VideoFfmpegDir);

        // --- Advanced: LoRA ---
        AddArgIfSet(args, "--lora", preset.Lora);
        AddArgIfSet(args, "--lora-scaled", preset.LoraScaled);
        AddArgIfSet(args, "--control-vector", preset.ControlVector);
        AddArgIfSet(args, "--control-vector-scaled", preset.ControlVectorScaled);
        if (preset.ControlVectorLayerStart.HasValue && preset.ControlVectorLayerEnd.HasValue)
        {
            args.Add("--control-vector-layer-range");
            args.Add(preset.ControlVectorLayerStart.Value.ToString(CultureInfo.InvariantCulture));
            args.Add(preset.ControlVectorLayerEnd.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (preset.LoraInitWithoutApply.HasValue && preset.LoraInitWithoutApply.Value)
            args.Add("--lora-init-without-apply");

        // --- Advanced: Server Behavior & Endpoints ---
        AddArgIfSet(args, "--alias", preset.ModelAlias);
        AddArgIfSet(args, "--tags", preset.ModelTags);
        if (preset.Embeddings.HasValue)
            args.Add(preset.Embeddings.Value ? "--embeddings" : "--no-embeddings");
        if (preset.Reranking.HasValue)
            args.Add(preset.Reranking.Value ? "--reranking" : "--no-reranking");
        AddArgIfSet(args, "--pooling", preset.Pooling);
        AddIntArg(args, "--embd-normalize", preset.EmbdNormalize);
        if (preset.SlotsEndpoint.HasValue)
            args.Add(preset.SlotsEndpoint.Value ? "--slots" : "--no-slots");
        if (preset.MetricsEndpoint.HasValue && preset.MetricsEndpoint.Value)
            args.Add("--metrics");
        if (preset.PropsEndpoint.HasValue)
            args.Add(preset.PropsEndpoint.Value ? "--props" : "--no-props");
        if (preset.WebUi.HasValue)
            args.Add(preset.WebUi.Value ? "--webui" : "--no-webui");
        AddArgIfSet(args, "--api-prefix", preset.ApiPrefix);
        AddIntArg(args, "--threads-http", preset.ThreadsHttp);
        AddIntArg(args, "--sse-ping-interval", preset.SsePingInterval);
        if (preset.ReusePort.HasValue && preset.ReusePort.Value)
            args.Add("--reuse-port");
        AddArgIfSet(args, "--slot-save-path", preset.SlotSavePath);
        AddArgIfSet(args, "--media-path", preset.MediaPath);
        if (preset.SwaFull.HasValue && preset.SwaFull.Value)
            args.Add("--swa-full");
        if (preset.CacheIdleSlots.HasValue)
            args.Add(preset.CacheIdleSlots.Value ? "--cache-idle-slots" : "--no-cache-idle-slots");
        AddIntArg(args, "--kv-unified-per-slot", preset.KvUnifiedPerSlot);
        AddIntArg(args, "--ctx-checkpoints", preset.CtxCheckpoints);
        if (preset.Warmup.HasValue)
            args.Add(preset.Warmup.Value ? "--warmup" : "--no-warmup");
        if (preset.SpmInfill.HasValue && preset.SpmInfill.Value)
            args.Add("--spm-infill");
        if (preset.SkipChatParsing.HasValue)
            args.Add(preset.SkipChatParsing.Value ? "--skip-chat-parsing" : "--no-skip-chat-parsing");
        if (preset.PrefillAssistant.HasValue)
            args.Add(preset.PrefillAssistant.Value ? "--prefill-assistant" : "--no-prefill-assistant");
        AddArgIfSet(args, "--numa", preset.Numa);
        AddArgIfSet(args, "--rpc", preset.RpcServers);
        AddRepeatedArg(args, "--override-kv", preset.OverrideKv);

        // --- Advanced: Security & CORS ---
        AddArgIfSet(args, "--cors-origins", preset.CorsOrigins);
        AddArgIfSet(args, "--cors-methods", preset.CorsMethods);
        AddArgIfSet(args, "--cors-headers", preset.CorsHeaders);
        if (preset.CorsCredentials.HasValue)
            args.Add(preset.CorsCredentials.Value ? "--cors-credentials" : "--no-cors-credentials");
        AddArgIfSet(args, "--api-key-file", preset.ApiKeyFile);
        AddArgIfSet(args, "--ssl-key-file", preset.SslKeyFile);
        AddArgIfSet(args, "--ssl-cert-file", preset.SslCertFile);

        // --- Advanced: Logging ---
        if (preset.LogDisable.HasValue && preset.LogDisable.Value)
            args.Add("--log-disable");
        AddArgIfSet(args, "--log-file", preset.LogFile);
        AddIntArg(args, "--log-verbosity", preset.LogVerbosity);
        AddArgIfSet(args, "--log-colors", preset.LogColors);
        if (preset.LogTimestamps.HasValue)
            args.Add(preset.LogTimestamps.Value ? "--log-timestamps" : "--no-log-timestamps");
        if (preset.LogPrefix.HasValue)
            args.Add(preset.LogPrefix.Value ? "--log-prefix" : "--no-log-prefix");
        if (preset.LogJsonl.HasValue)
            args.Add(preset.LogJsonl.Value ? "--log-jsonl" : "--no-log-jsonl");
        AddArgIfSet(args, "--log-prompts-dir", preset.LogPromptsDir);

        // --- Advanced: Chat Template ---
        AddArgIfSet(args, "--chat-template", preset.ChatTemplate);
        AddArgIfSet(args, "--chat-template-kwargs", preset.ChatTemplateKwargs);

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

    /// <summary>
    /// Emits one "name value" pair per comma-separated entry in <paramref name="value"/> — for
    /// flags like --dry-sequence-breaker that llama.cpp expects repeated once per item rather
    /// than as a single comma-separated argument.
    /// </summary>
    private static void AddRepeatedArg(List<string> args, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        foreach (var entry in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            args.Add(name);
            args.Add(entry);
        }
    }

    private static void AddIntArg(List<string> args, string name, int? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddLongArg(List<string> args, string name, long? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddFloatArg(List<string> args, string name, float? value)
    {
        if (value.HasValue)
        {
            args.Add(name);
            args.Add(value.Value.ToString("G7", CultureInfo.InvariantCulture));
        }
    }
}
