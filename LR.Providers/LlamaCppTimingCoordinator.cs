using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Coordinates stdout timing data from llama.cpp with HTTP request/response lifecycle.
/// Assigns task_ids to pending requests (FIFO) and merges completion timing into RouteResponse objects.
/// </summary>
public class LlamaCppTimingCoordinator
{
    private readonly ILogger<LlamaCppTimingCoordinator> _logger;

    /// <summary>
    /// Pending requests waiting to be assigned a task_id from stdout.
    /// Each entry holds the RouteResponse being built and the enqueue time.
    /// </summary>
    private readonly ConcurrentQueue<(DateTimeOffset EnqueueTime, RouteResponse Response)> _pendingRequests = new();

    /// <summary>
    /// Active requests mapped by llama.cpp task_id to their RouteResponse.
    /// Used to merge timing data into the correct response when completion lines appear.
    /// </summary>
    private readonly ConcurrentDictionary<int, (RouteResponse Response, DateTimeOffset StartTime)> _activeRequests = new();

    /// <summary>
    /// Accumulated timing data per task_id from stdout parsing.
    /// Updated incrementally as print_timing lines arrive.
    /// </summary>
    private readonly ConcurrentDictionary<int, LlamaCppTaskTiming> _taskTimings = new();

    public LlamaCppTimingCoordinator(ILogger<LlamaCppTimingCoordinator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a pending request waiting to be assigned a task_id from stdout.
    /// </summary>
    public void EnqueuePending(DateTimeOffset enqueueTime, RouteResponse response)
    {
        _pendingRequests.Enqueue((enqueueTime, response));
    }

    /// <summary>
    /// Processes a parsed timing event from stdout.
    /// Assigns new task_ids to pending requests (FIFO) and merges completion data into active responses.
    /// </summary>
    public void ProcessEvent(LlamaCppTimingEvent evt)
    {
        // Get or create the accumulated timing for this task
        var isNew = !_taskTimings.ContainsKey(evt.TaskId);
        var timing = _taskTimings.GetOrAdd(evt.TaskId, _ => new LlamaCppTaskTiming { TaskId = evt.TaskId });

        if (isNew)
            _logger.LogInformation("[Stats] New stdout task detected: TaskId={TaskId}, Phase={Phase}", evt.TaskId, evt.Phase);

        // Assign the task_id to the oldest pending request on the FIRST stdout line seen for it,
        // regardless of phase. Previously this only happened on PromptProcessing/Generation
        // progress lines, which llama.cpp only prints periodically (per-batch / every ~3s) — a
        // request whose prompt fits in one batch and whose generation finishes before the next
        // progress tick never printed one, so its first (and possibly only) stdout line was a
        // Completion summary. Since assignment never ran, the completion data below had nowhere
        // to merge into and was silently dropped, leaving PromptProcessingMs/GenerationMs at 0
        // despite token counts and total latency being recorded correctly. (AssignTaskToPendingRequest
        // is idempotent — a no-op if this task_id is already assigned.)
        AssignTaskToPendingRequest(evt.TaskId);

        switch (evt.Phase)
        {
            case LlamaCppTimingPhase.PromptProcessing:
                timing.PromptProgress = evt.Progress ?? 0;
                break;

            case LlamaCppTimingPhase.Generation:
                timing.NDecoded = evt.NDecoded;
                break;

            case LlamaCppTimingPhase.Completion:
                ApplyCompletionEvent(timing, evt);
                _logger.LogInformation("[Stats] Completion event for TaskId={TaskId}: PromptEvalMs={PromptEvalMs}, EvalMs={EvalMs}, TotalMs={TotalMs}",
                    evt.TaskId, evt.PromptEvalMs, evt.EvalMs, evt.TotalMs);
                MergeTimingIntoActiveRequest(evt.TaskId, timing);
                break;
        }
    }

    /// <summary>
    /// Merges any available stdout timing data into a RouteResponse.
    /// Used by SendRequestAsync/SendStreamRequestAsync after the HTTP response completes.
    /// </summary>
    public void MergeTimingData(RouteResponse response)
    {
        bool foundInActive = false;

        // Find which task_id this response is associated with
        foreach (var kvp in _activeRequests)
        {
            if (ReferenceEquals(kvp.Value.Response, response))
            {
                var timing = _taskTimings.GetValueOrDefault(kvp.Key);
                if (timing != null)
                    MergeTimingIntoActiveRequest(kvp.Key, timing);
                else
                    _logger.LogWarning("[Stats] Task {TaskId} found in active requests but NO timing data available", kvp.Key);

                // Clean up the active request entry
                _activeRequests.TryRemove(kvp.Key, out _);
                foundInActive = true;
                break;
            }
        }

        if (!foundInActive)
        {
            var activeTaskIds = string.Join(",", _activeRequests.Keys);
            var timingKeys = string.Join(",", _taskTimings.Keys);
            _logger.LogWarning("[Stats] Response NOT found in active requests! Active={_ActiveCount} (tasks: {ActiveTasks}), Pending={_PendingCount}, Timing entries: {TimingEntries}",
                _activeRequests.Count, activeTaskIds, _pendingRequests.Count, timingKeys);
        }

        // Also try to remove from pending queue if it wasn't assigned a task yet
        var itemsToRequeue = new System.Collections.Generic.List<(DateTimeOffset, RouteResponse)>();
        bool foundAndRemoved = false;
        while (_pendingRequests.TryDequeue(out var item))
        {
            if (ReferenceEquals(item.Response, response) && !foundAndRemoved)
                foundAndRemoved = true; // skip this one
            else
                itemsToRequeue.Add(item);
        }
        foreach (var item in itemsToRequeue)
            _pendingRequests.Enqueue(item);
    }

    /// <summary>
    /// Assigns a newly seen task_id to the oldest pending request (FIFO).
    /// </summary>
    private void AssignTaskToPendingRequest(int taskId)
    {
        // Check if already assigned
        if (_activeRequests.ContainsKey(taskId))
            return;

        // Try to dequeue a pending request
        while (_pendingRequests.TryDequeue(out var pending))
        {
            _activeRequests[taskId] = (pending.Response, pending.EnqueueTime);
            _logger.LogInformation("[Stats] Assigned task {TaskId} to request. Active={_ActiveCount}, Pending={_PendingCount}",
                taskId, _activeRequests.Count, _pendingRequests.Count);
            return;
        }

        // If we get here, no pending request was found for this task_id
        if (!_activeRequests.ContainsKey(taskId))
            _logger.LogWarning("[Stats] No pending request found for stdout task {TaskId}! Pending queue empty. This means a task appeared before any request was enqueued.", taskId);
    }

    /// <summary>
    /// Applies a completion summary event's data into the accumulated LlamaCppTaskTiming.
    /// </summary>
    private static void ApplyCompletionEvent(LlamaCppTaskTiming timing, LlamaCppTimingEvent evt)
    {
        if (evt.PromptEvalMs.HasValue)
        {
            timing.PromptEvalMs = evt.PromptEvalMs;
            timing.PromptTokens = evt.PromptTokens;
            timing.PromptTokensPerSec = evt.PromptTokensPerSec;
        }

        if (evt.EvalMs.HasValue)
        {
            timing.EvalMs = evt.EvalMs;
            timing.GeneratedTokens = evt.GeneratedTokens;
            timing.GenTokensPerSec = evt.GenTokensPerSecCompletion;
        }

        if (evt.TotalMs.HasValue)
            timing.TotalMs = evt.TotalMs;

        if (evt.DraftAcceptanceRate.HasValue)
        {
            timing.DraftAcceptanceRate = evt.DraftAcceptanceRate;
            timing.DraftAccepted = evt.DraftAccepted;
            timing.DraftGenerated = evt.DraftGenerated;
            timing.DraftMeanLen = evt.DraftMeanLen;
        }
    }

    /// <summary>
    /// Merges accumulated stdout timing data into the RouteResponse for an active request.
    /// </summary>
    private void MergeTimingIntoActiveRequest(int taskId, LlamaCppTaskTiming timing)
    {
        if (!_activeRequests.TryGetValue(taskId, out var entry))
            return;

        var response = entry.Response;

        // Only overwrite timing values from stdout if they're non-zero (stdout data is authoritative here).
        if (timing.PromptEvalMs.HasValue && timing.PromptEvalMs.Value > 0)
            response.PromptProcessingMs = timing.PromptEvalMs.Value;

        // llama.cpp prints the prompt-processing rate it computed itself ("... / 91.54 tokens per
        // second"), derived from the tokens it actually ran through the model. Prefer that over
        // recomputing from PromptTokensProcessed (which includes cache hits) / PromptProcessingMs.
        if (timing.PromptTokensPerSec.HasValue && timing.PromptTokensPerSec.Value > 0)
            response.PromptTokensPerSecond = timing.PromptTokensPerSec.Value;

        if (timing.EvalMs.HasValue && timing.EvalMs.Value > 0)
            response.GenerationMs = timing.EvalMs.Value;

        // Total latency from stdout is the most accurate.
        if (timing.TotalMs.HasValue && timing.TotalMs.Value > 0)
            response.TotalLatencyMs = timing.TotalMs.Value;
        else if (response.PromptProcessingMs > 0 || response.GenerationMs > 0)
            response.TotalLatencyMs = response.PromptProcessingMs + response.GenerationMs;

        // Speculative decoding metrics (only populated when speculative decoding is active)
        if (timing.DraftAcceptanceRate.HasValue && timing.DraftAcceptanceRate.Value > 0)
        {
            response.DraftAcceptanceRate = timing.DraftAcceptanceRate;
            response.DraftAccepted = timing.DraftAccepted;
            response.DraftGenerated = timing.DraftGenerated;
            response.DraftMeanLen = timing.DraftMeanLen;
        }

        _logger.LogInformation("[Stats] Merged timing for task {TaskId}: Prompt={PromptMs:F0}ms, Gen={GenMs:F0}ms, Total={TotalMs:F0}ms",
            taskId, response.PromptProcessingMs, response.GenerationMs, response.TotalLatencyMs);
    }
}
