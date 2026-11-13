using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class PresetLoadStateTests
{
    private static readonly Guid PresetId = Guid.NewGuid();

    private static ServerInstance Server(ServerStatus status, bool healthy = true, Guid? activePresetId = null) => new()
    {
        Status = status,
        IsHealthy = healthy,
        ActivePresetId = activePresetId ?? PresetId,
    };

    [Theory]
    [InlineData(ServerStatus.Running, true, PresetLoadState.Loaded)]
    [InlineData(ServerStatus.Running, false, PresetLoadState.Loading)]
    [InlineData(ServerStatus.Starting, false, PresetLoadState.Loading)]
    [InlineData(ServerStatus.Reconnecting, false, PresetLoadState.Loading)]
    [InlineData(ServerStatus.Error, false, PresetLoadState.Error)]
    [InlineData(ServerStatus.Idle, false, PresetLoadState.Unloaded)]
    [InlineData(ServerStatus.Stopping, true, PresetLoadState.Unloaded)]
    public void ReflectsServerStatus_WhenServingThePreset(ServerStatus status, bool healthy, string expected)
    {
        Assert.Equal(expected, PresetLoadState.Of(Server(status, healthy), PresetId));
    }

    [Fact]
    public void Unloaded_WhenServerHasAnotherPresetLoaded()
    {
        Assert.Equal(PresetLoadState.Unloaded, PresetLoadState.Of(Server(ServerStatus.Running, activePresetId: Guid.NewGuid()), PresetId));
    }

    [Fact]
    public void Unloaded_WhenServerIsMissing()
    {
        Assert.Equal(PresetLoadState.Unloaded, PresetLoadState.Of(null, PresetId));
    }
}
