using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

/// <summary>
/// Covers <see cref="RoutingEngine"/>'s preset fallback chain: when it kicks in (primary
/// errored, or primary busy with the fallback already loaded), when it must not (key scoping,
/// same-server fallbacks), and that the request is retargeted at the preset that serves it.
/// </summary>
public class RoutingEngineFallbackTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly FakeServerManager _serverManager;
    private readonly ApiKeyRequestContext _keyContext = new();

    public RoutingEngineFallbackTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _serverManager = new FakeServerManager(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private RoutingEngine Engine() => new(_context, _serverManager, new ServerConcurrencyLimiter(), new GatewaySettings(), _keyContext);

    private ServerInstance Server(string name, ServerStatus status, bool healthy = true)
    {
        var s = new ServerInstance { Name = name, Status = status, IsHealthy = healthy };
        _context.ServerInstances.Add(s);
        _context.SaveChanges();
        return s;
    }

    private ModelPreset Preset(string name, ServerInstance server, ModelPreset? fallback = null)
    {
        var p = new ModelPreset { Name = name, ServerInstanceId = server.Id, ModelPath = $"/{name}.gguf", FallbackPresetId = fallback?.Id };
        _context.ModelPresets.Add(p);
        _context.SaveChanges();
        return p;
    }

    private static void Load(ServerInstance server, ModelPreset preset)
    {
        server.Status = ServerStatus.Running;
        server.IsHealthy = true;
        server.ActivePresetId = preset.Id;
    }

    [Fact]
    public async Task PrimaryHealthy_ServesPrimary()
    {
        var s1 = Server("s1", ServerStatus.Running);
        var s2 = Server("s2", ServerStatus.Running);
        var b = Preset("b", s2);
        var a = Preset("a", s1, fallback: b);
        Load(s1, a);
        Load(s2, b);

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        using var decision = await Engine().RouteAsync(request);

        Assert.Equal(s1.Id, decision?.Server.Id);
        Assert.Equal(a.Id, request.PresetId);
    }

    [Fact]
    public async Task PrimaryErrored_LoadedFallbackServes_AndRequestIsRetargeted()
    {
        var s1 = Server("s1", ServerStatus.Error, healthy: false);
        var s2 = Server("s2", ServerStatus.Running);
        var b = Preset("b", s2);
        var a = Preset("a", s1, fallback: b);
        Load(s2, b);

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        using var decision = await Engine().RouteAsync(request);

        Assert.Equal(s2.Id, decision?.Server.Id);
        Assert.Equal(b.Id, request.PresetId);
    }

    [Fact]
    public async Task PrimaryErrored_IdleFallbackIsStarted_AndRequestWaitsForIt()
    {
        var s1 = Server("s1", ServerStatus.Error, healthy: false);
        var s2 = Server("s2", ServerStatus.Idle, healthy: false);
        var b = Preset("b", s2);
        var a = Preset("a", s1, fallback: b);

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        var decision = await Engine().RouteAsync(request);

        Assert.Null(decision);
        Assert.Contains(s2.Id, _serverManager.AutoStarted);
        Assert.Equal(b.Id, s2.ActivePresetId);
        Assert.Equal(b.Id, request.PresetId);
    }

    [Fact]
    public async Task PrimaryStarting_IdleFallbackIsNotStarted()
    {
        var s1 = Server("s1", ServerStatus.Starting, healthy: false);
        var s2 = Server("s2", ServerStatus.Idle, healthy: false);
        var b = Preset("b", s2);
        var a = Preset("a", s1, fallback: b);
        s1.ActivePresetId = a.Id;

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        var decision = await Engine().RouteAsync(request);

        Assert.Null(decision);
        Assert.DoesNotContain(s2.Id, _serverManager.AutoStarted);
        Assert.Equal(a.Id, request.PresetId);
    }

    [Fact]
    public async Task FallbackChain_SkipsUnloadedHop_InOverflowCase()
    {
        // a is (re)starting; b's server is idle (not used for overflow); c is loaded → serves.
        var s1 = Server("s1", ServerStatus.Starting, healthy: false);
        var s2 = Server("s2", ServerStatus.Idle, healthy: false);
        var s3 = Server("s3", ServerStatus.Running);
        var c = Preset("c", s3);
        var b = Preset("b", s2, fallback: c);
        var a = Preset("a", s1, fallback: b);
        s1.ActivePresetId = a.Id;
        Load(s3, c);

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        using var decision = await Engine().RouteAsync(request);

        Assert.Equal(s3.Id, decision?.Server.Id);
        Assert.Equal(c.Id, request.PresetId);
    }

    [Fact]
    public async Task FallbackNotAllowedForApiKey_IsSkipped()
    {
        var s1 = Server("s1", ServerStatus.Error, healthy: false);
        var s2 = Server("s2", ServerStatus.Running);
        var b = Preset("b", s2);
        var a = Preset("a", s1, fallback: b);
        Load(s2, b);
        _keyContext.CurrentKey = new ApiKey { AllowAllModels = false, AllowedPresets = [new ApiKeyModelPreset { ModelPresetId = a.Id }] };

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        var decision = await Engine().RouteAsync(request);

        Assert.Null(decision);
        Assert.Equal(a.Id, request.PresetId);
    }

    [Fact]
    public async Task FallbackOnSameServer_IsSkipped()
    {
        var s1 = Server("s1", ServerStatus.Error, healthy: false);
        var b = Preset("b", s1);
        var a = Preset("a", s1, fallback: b);

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        var decision = await Engine().RouteAsync(request);

        Assert.Null(decision);
        Assert.Equal(a.Id, request.PresetId);
    }

    [Fact]
    public async Task FallbackCycle_Terminates()
    {
        var s1 = Server("s1", ServerStatus.Error, healthy: false);
        var s2 = Server("s2", ServerStatus.Error, healthy: false);
        var a = Preset("a", s1);
        var b = Preset("b", s2, fallback: a);
        a.FallbackPresetId = b.Id;
        _context.SaveChanges();

        var request = new RouteRequest { ModelName = "a", PresetId = a.Id };
        var decision = await Engine().RouteAsync(request);

        Assert.Null(decision);
    }

    [Fact]
    public async Task Alias_ResolvesToPreset()
    {
        var s1 = Server("s1", ServerStatus.Running);
        var a = Preset("a", s1);
        a.Aliases = "gpt-*";
        _context.SaveChanges();
        Load(s1, a);

        using var decision = await Engine().RouteAsync(new RouteRequest { ModelName = "gpt-4o" });

        Assert.Equal(s1.Id, decision?.Server.Id);
    }

    /// <summary>
    /// Serves instances straight from the test's DbContext (tracked entities, so status changes
    /// made by the engine are visible to the test) and records auto-starts without starting
    /// anything. No providers are registered, so every reservation is a no-op lease.
    /// </summary>
    private sealed class FakeServerManager(LRDbContext context) : IServerManager
    {
        public List<Guid> AutoStarted { get; } = [];

        public Task<bool> TryAutoStartAsync(Guid instanceId, CancellationToken cancellationToken = default)
        {
            AutoStarted.Add(instanceId);
            return Task.FromResult(false);
        }

        public Task<bool> RestartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<ServerInstance?> GetHealthAsync(Guid instanceId) => Task.FromResult(context.ServerInstances.Find(instanceId));
        public IReadOnlyList<ServerInstance> GetAllInstances() => context.ServerInstances.ToList();
        public Task<IReadOnlyList<ServerInstance>> GetAllInstancesAsync() => Task.FromResult(GetAllInstances());
        public IBackendProvider? GetProvider(Guid instanceId) => null;

        public Task<ServerInstance> CreateInstanceAsync(string name, ServerEngine engine, BackendConfigData configData, int? port = null) => throw new NotImplementedException();
        public Task<bool> StartAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> StartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BackendConfig> UpdateBackendConfigAsync(Guid instanceId, BackendConfigData configData) => throw new NotImplementedException();
        public Task<string?> GetStartCommandAsync(Guid instanceId) => throw new NotImplementedException();
        public Task StopAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemoveInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateHealthAsync(Guid instanceId, bool isHealthy) => throw new NotImplementedException();
    }
}
