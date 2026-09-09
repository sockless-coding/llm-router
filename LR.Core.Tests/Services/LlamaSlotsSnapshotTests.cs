using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class LlamaSlotsSnapshotTests
{
    // Shape taken from a real llama.cpp /slots response: 4 slots, one processing (id 1) with a
    // 35208-token cached prompt and 498 tokens generated so far, the rest idle. n_ctx 128000 each.
    private const string RealShape = """
        [
          {"id":0,"n_ctx":128000,"is_processing":false},
          {"id":1,"n_ctx":128000,"is_processing":true,"id_task":1362,
           "n_prompt_tokens":35208,"n_prompt_tokens_processed":2331,"n_prompt_tokens_cache":32376,
           "next_token":[{"has_next_token":true,"n_remain":63502,"n_decoded":498}]},
          {"id":2,"n_ctx":128000,"is_processing":false,"n_prompt_tokens":0,
           "next_token":[{"has_next_token":false,"n_remain":-1,"n_decoded":0}]},
          {"id":3,"n_ctx":128000,"is_processing":false,"n_prompt_tokens":0,
           "next_token":[{"has_next_token":false,"n_remain":-1,"n_decoded":0}]}
        ]
        """;

    [Fact]
    public void FromJson_AggregatesBusiestSlotAndTotals()
    {
        var usage = LlamaSlotsSnapshot.FromJson(RealShape);

        Assert.NotNull(usage);
        Assert.Equal(4, usage!.TotalSlots);
        Assert.Equal(1, usage.BusySlots);
        Assert.Equal(35208 + 498, usage.UsedTokens);
        Assert.Equal(128000 * 4, usage.ContextLimit);

        // Busiest slot: 35706 / 128000 ≈ 0.279
        Assert.Equal(35706.0 / 128000.0, usage.BusiestSlotUsageRatio!.Value, precision: 4);
        // Aggregate is far lower — only one of four slots holds tokens.
        Assert.Equal(35706.0 / 512000.0, usage.AggregateUsageRatio!.Value, precision: 4);
    }

    [Fact]
    public void FromJson_AcceptsSlotsWrapperObject()
    {
        var usage = LlamaSlotsSnapshot.FromJson("""{"slots":[{"id":0,"n_ctx":1000,"is_processing":true,"n_prompt_tokens":250}]}""");

        Assert.NotNull(usage);
        Assert.Equal(0.25, usage!.BusiestSlotUsageRatio!.Value, precision: 5);
        Assert.Equal(250, usage.UsedTokens);
    }

    [Fact]
    public void FromJson_AllSlotsIdle_ReportsZeroUsage()
    {
        var usage = LlamaSlotsSnapshot.FromJson("""[{"id":0,"n_ctx":4096,"is_processing":false}]""");

        Assert.NotNull(usage);
        Assert.Equal(0, usage!.BusiestSlotUsageRatio);
        Assert.Equal(0, usage.UsedTokens);
        Assert.Equal(0, usage.BusySlots);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"slots endpoint disabled\"}")]
    public void FromJson_ReturnsNull_OnUnusableBody(string? body)
    {
        Assert.Null(LlamaSlotsSnapshot.FromJson(body));
    }
}
