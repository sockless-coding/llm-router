using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class LlamaSlotCapacityTests
{
    private static GatewaySettings Settings(bool enabled, int thresholdPct = 90) => new()
    {
        ContextAwareQueuing = enabled,
        ContextUsageQueueThresholdPercent = thresholdPct,
    };

    private static CapacityProviderStub Provider(double? busiestSlotRatio) => new()
    {
        RuntimeUsage = busiestSlotRatio is double r
            ? new LlamaRuntimeUsage { BusiestSlotUsageRatio = r }
            : null,
    };

    [Fact]
    public void ShouldHoldForContext_False_WhenFeatureDisabled()
    {
        Assert.False(LlamaSlotCapacity.ShouldHoldForContext(Provider(0.99), Settings(enabled: false), inFlight: 3));
    }

    [Fact]
    public void ShouldHoldForContext_False_WhenNothingInFlight()
    {
        // Anti-deadlock rule: an idle server is always admitted so usage can drain.
        Assert.False(LlamaSlotCapacity.ShouldHoldForContext(Provider(0.99), Settings(enabled: true), inFlight: 0));
    }

    [Fact]
    public void ShouldHoldForContext_False_WhenUsageBelowThreshold()
    {
        Assert.False(LlamaSlotCapacity.ShouldHoldForContext(Provider(0.80), Settings(enabled: true, thresholdPct: 90), inFlight: 1));
    }

    [Fact]
    public void ShouldHoldForContext_True_WhenUsageAtOrAboveThreshold_AndBusy()
    {
        Assert.True(LlamaSlotCapacity.ShouldHoldForContext(Provider(0.90), Settings(enabled: true, thresholdPct: 90), inFlight: 1));
        Assert.True(LlamaSlotCapacity.ShouldHoldForContext(Provider(0.97), Settings(enabled: true, thresholdPct: 90), inFlight: 2));
    }

    [Fact]
    public void ShouldHoldForContext_False_WhenUsageUnknown()
    {
        Assert.False(LlamaSlotCapacity.ShouldHoldForContext(Provider(null), Settings(enabled: true), inFlight: 2));
    }

    [Fact]
    public void ShouldHoldForContext_False_WhenProviderIsNull()
    {
        Assert.False(LlamaSlotCapacity.ShouldHoldForContext(null, Settings(enabled: true), inFlight: 2));
    }

    /// <summary>Bare double implementing the two interfaces <c>ShouldHoldForContext</c> looks at.</summary>
    private sealed class CapacityProviderStub : IBackendProvider, IServerCapacityProvider
    {
        public LlamaRuntimeUsage? RuntimeUsage { get; init; }
        public LlamaServerProps? ServerProps => null;
        public int? MaxConcurrentRequests => null;
        public Task RefreshRuntimeUsageAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ServerEngine Engine => ServerEngine.LlamaCpp;
        public Task<bool> StartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task StopProcessAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RestartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> TryReconnectAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<RouteResponse?> SendRequestAsync(string payload, ApiProtocol protocol = ApiProtocol.OpenAI, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<RouteStreamChunk> SendStreamRequestAsync(string payload, ApiProtocol protocol = ApiProtocol.OpenAI, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Configure(BackendConfigData configData) => throw new NotImplementedException();
        public void SetServerInstance(ServerInstance? instance) => throw new NotImplementedException();
        public string? GetStartCommand(ModelPreset preset, int? port = null) => throw new NotImplementedException();
    }
}
