namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Executes an ordered list of <see cref="IBuildStep"/>s against a <see cref="BuildContext"/>,
/// emitting phase-boundary markers. Both the release-install and source-compile pipelines are just
/// a list handed to this method, so the sequencing/logging/cancellation handling lives in one place.
/// </summary>
public static class EngineBuildRunner
{
    public static async Task RunAsync(IReadOnlyList<IBuildStep> pipeline, BuildContext ctx, CancellationToken ct)
    {
        for (int i = 0; i < pipeline.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = pipeline[i];
            await ctx.Sink.LineAsync(step.Phase, $"▶ step {i + 1}/{pipeline.Count}: {step.Phase}");
            try
            {
                await step.ExecuteAsync(ctx, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ctx.Sink.LineAsync(step.Phase, $"✖ {step.Phase} failed: {ex.Message}");
                throw;
            }
            await ctx.Sink.LineAsync(step.Phase, $"✔ {step.Phase}");
        }
    }
}
