using System.Globalization;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Parses llama.cpp stdout print_timing lines into structured timing events.
/// Handles prompt processing, generation progress, and completion summary lines.
/// </summary>
public class LlamaCppStdoutParser
{
    // --- Prompt processing line ---
    // 657.15.340.849 I slot print_timing: id  0 | task 120879 | prompt processing, n_tokens =   2048, progress = 0.09, t =   3.33 s / 615.51 tokens per second
    private static readonly Regex PromptProcessingRegex =
        new(
            @"print_timing:\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<taskId>\d+)\s*\|\s*prompt processing,\s*n_tokens\s*=\s*(?<tokens>\d+),\s*progress\s*=\s*(?<progress>[\d.]+),\s*t\s*=\s*(?<timeMs>[\d.]+) s / (?<tps>[\d.]+) tokens per second",
            RegexOptions.Compiled);

    // --- Generation progress line ---
    // 665.03.774.412 I slot print_timing: id  0 | task 121574 | n_decoded =    102, tg =  25.35 t/s, tg_3s =  25.34 t/s
    private static readonly Regex GenerationProgressRegex =
        new(
            @"print_timing:\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<taskId>\d+)\s*\|\s*n_decoded\s*=\s*(?<decoded>\d+),\s*tg\s*=\s*(?<tg>[\d.]+) t/s,\s*tg_3s\s*=\s*(?<tg3s>[\d.]+) t/s",
            RegexOptions.Compiled);

    // --- Completion: prompt eval time ---
    // 665.18.892.620 I slot print_timing: id  0 | task 121574 | prompt eval time =    2403.38 ms /   220 tokens (   10.92 ms per token,    91.54 tokens per second)
    private static readonly Regex PromptEvalTimeRegex =
        new(
            @"print_timing:\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<taskId>\d+)\s*\|\s*prompt eval time\s*=\s*(?<timeMs>[\d.]+) ms /\s+(?<tokens>\d+) tokens \(\s*\(\s*\(\s*\(\s*\(\s*(?<msPerToken>[\d.]+) ms per token,\s*(?<tps>[\d.]+) tokens per second\)",
            RegexOptions.Compiled);

    // --- Completion: eval time ---
    // 665.18.892.625 I slot print_timing: id  0 | task 121574 |        eval time =   19142.70 ms /   468 tokens (   40.90 ms per token,    24.45 tokens per second)
    private static readonly Regex EvalTimeRegex =
        new(
            @"print_timing:\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<taskId>\d+)\s*\|\s+eval time\s*=\s*(?<timeMs>[\d.]+) ms /\s+(?<tokens>\d+) tokens \(\s*\(\s*\(\s*\(\s*\(\s*(?<msPerToken>[\d.]+) ms per token,\s*(?<tps>[\d.]+) tokens per second\)",
            RegexOptions.Compiled);

    // --- Completion: total time ---
    // 665.18.892.626 I slot print_timing: id  0 | task 121574 |       total time =   21546.08 ms /   688 tokens
    private static readonly Regex TotalTimeRegex =
        new(
            @"print_timing:\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<taskId>\d+)\s*\|\s+total time\s*=\s*(?<timeMs>[\d.]+) ms /\s+(?<tokens>\d+) tokens",
            RegexOptions.Compiled);

    // --- Completion: draft acceptance (speculative decoding only) ---
    // 665.18.892.631 I slot print_timing: id  0 | task 121574 | draft acceptance = 0.56000 (  294 accepted /   525 generated), mean len =  2.68
    private static readonly Regex DraftAcceptanceRegex =
        new(
            @"print_timing:\s+id\s+(?<slot>\d+)\s*\|\s*task\s+(?<taskId>\d+)\s*\|\s*draft acceptance\s*=\s*(?<rate>[\d.]+) \(\s*(?<accepted>\d+) accepted /\s*(?<generated>\d+) generated\), mean len\s*=\s*(?<meanLen>[\d.]+)",
            RegexOptions.Compiled);

    /// <summary>
    /// Parses a single stdout line from llama.cpp.
    /// Returns a timing event if the line is a print_timing line, null otherwise.
    /// </summary>
    public LlamaCppTimingEvent? ParseLine(string line)
    {
        var ci = CultureInfo.InvariantCulture;

        // Try prompt processing progress
        var match = PromptProcessingRegex.Match(line);
        if (match.Success)
            return new LlamaCppTimingEvent
            {
                TaskId = int.Parse(match.Groups["taskId"].Value, ci),
                Phase = LlamaCppTimingPhase.PromptProcessing,
                NTokens = int.Parse(match.Groups["tokens"].Value, ci),
                Progress = double.Parse(match.Groups["progress"].Value, ci),
                TokensPerSec = double.Parse(match.Groups["tps"].Value, ci)
            };

        // Try generation progress
        match = GenerationProgressRegex.Match(line);
        if (match.Success)
            return new LlamaCppTimingEvent
            {
                TaskId = int.Parse(match.Groups["taskId"].Value, ci),
                Phase = LlamaCppTimingPhase.Generation,
                NDecoded = int.Parse(match.Groups["decoded"].Value, ci),
                GenTokensPerSec = double.Parse(match.Groups["tg"].Value, ci),
                Gen3sTokensPerSec = double.Parse(match.Groups["tg3s"].Value, ci)
            };

        // Try prompt eval time (completion summary)
        match = PromptEvalTimeRegex.Match(line);
        if (match.Success)
            return new LlamaCppTimingEvent
            {
                TaskId = int.Parse(match.Groups["taskId"].Value, ci),
                Phase = LlamaCppTimingPhase.Completion,
                PromptEvalMs = double.Parse(match.Groups["timeMs"].Value, ci),
                PromptTokens = int.Parse(match.Groups["tokens"].Value, ci),
                PromptMsPerToken = double.Parse(match.Groups["msPerToken"].Value, ci),
                PromptTokensPerSec = double.Parse(match.Groups["tps"].Value, ci)
            };

        // Try eval time (completion summary)
        match = EvalTimeRegex.Match(line);
        if (match.Success)
            return new LlamaCppTimingEvent
            {
                TaskId = int.Parse(match.Groups["taskId"].Value, ci),
                Phase = LlamaCppTimingPhase.Completion,
                EvalMs = double.Parse(match.Groups["timeMs"].Value, ci),
                GeneratedTokens = int.Parse(match.Groups["tokens"].Value, ci),
                GenMsPerToken = double.Parse(match.Groups["msPerToken"].Value, ci),
                GenTokensPerSecCompletion = double.Parse(match.Groups["tps"].Value, ci)
            };

        // Try total time (completion summary)
        match = TotalTimeRegex.Match(line);
        if (match.Success)
            return new LlamaCppTimingEvent
            {
                TaskId = int.Parse(match.Groups["taskId"].Value, ci),
                Phase = LlamaCppTimingPhase.Completion,
                TotalMs = double.Parse(match.Groups["timeMs"].Value, ci),
                TotalTokens = int.Parse(match.Groups["tokens"].Value, ci)
            };

        // Try draft acceptance (completion summary, speculative decoding only)
        match = DraftAcceptanceRegex.Match(line);
        if (match.Success)
            return new LlamaCppTimingEvent
            {
                TaskId = int.Parse(match.Groups["taskId"].Value, ci),
                Phase = LlamaCppTimingPhase.Completion,
                DraftAcceptanceRate = double.Parse(match.Groups["rate"].Value, ci),
                DraftAccepted = int.Parse(match.Groups["accepted"].Value, ci),
                DraftGenerated = int.Parse(match.Groups["generated"].Value, ci),
                DraftMeanLen = double.Parse(match.Groups["meanLen"].Value, ci)
            };

        // Not a timing line we recognize
        return null;
    }
}