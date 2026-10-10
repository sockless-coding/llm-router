using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class PresetMemoryEstimatorTests
{
    private const long Mb = 1024 * 1024;
    private const long Overhead = 512 * Mb;

    private static long? Sizes(string path) => path switch
    {
        "/m.gguf" => 1000 * Mb,
        "/big-00001-of-00002.gguf" => 600 * Mb,
        "/big-00002-of-00002.gguf" => 400 * Mb,
        "/draft.gguf" => 100 * Mb,
        "/mmproj.gguf" => 50 * Mb,
        _ => null,
    };

    [Fact]
    public void Override_WinsOverEverything()
    {
        var preset = new ModelPreset { ModelPath = "/m.gguf", MemoryEstimateMb = 1234 };
        Assert.Equal(1234 * Mb, PresetMemoryEstimator.EstimateBytes(preset, null, Sizes));
    }

    [Fact]
    public void Weights_PlusOverhead_WithoutMetadata()
    {
        var preset = new ModelPreset { ModelPath = "/m.gguf" };
        Assert.Equal(1000 * Mb + 50 * Mb + Overhead, PresetMemoryEstimator.EstimateBytes(preset, null, Sizes));
    }

    [Fact]
    public void SplitShards_DraftAndProjector_AreSummed()
    {
        var preset = new ModelPreset { ModelPath = "/big-00001-of-00002.gguf", SpecDraftModel = "/draft.gguf", Mmproj = "/mmproj.gguf" };
        long weights = 1150 * Mb;
        Assert.Equal(weights + (long)(weights * 0.05) + Overhead, PresetMemoryEstimator.EstimateBytes(preset, null, Sizes));
    }

    [Fact]
    public void PartialOffload_ScalesWeights()
    {
        var preset = new ModelPreset { ModelPath = "/m.gguf", GpuLayers = 10, KvOffload = false };
        var model = new LocalModel { BlockCount = 40 };
        long weights = 250 * Mb;
        Assert.Equal(weights + (long)(weights * 0.05) + Overhead, PresetMemoryEstimator.EstimateBytes(preset, model, Sizes));
    }

    [Fact]
    public void KvCache_FromMetadata()
    {
        // 32 layers × 8 KV heads × 128 head dim × (2 + 2 bytes) × 4096 ctx = 512 MB
        var preset = new ModelPreset { ModelPath = "/missing.gguf", ContextSize = 4096 };
        var model = new LocalModel { BlockCount = 32, HeadCount = 32, KvHeadCount = 8, EmbeddingLength = 4096 };
        Assert.Equal(512 * Mb + Overhead, PresetMemoryEstimator.EstimateBytes(preset, model, Sizes));

        preset.CacheTypeK = preset.CacheTypeV = "q8_0";
        Assert.Equal((long)(512 * Mb * (34.0 / 32) / 2) + Overhead, PresetMemoryEstimator.EstimateBytes(preset, model, Sizes));
    }
}

public class MemoryGroupSchedulerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly ServerConcurrencyLimiter _limiter = new();
    private readonly List<Guid> _stopped = [];

    public MemoryGroupSchedulerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _context.MemoryGroups.Add(new MemoryGroup { Name = "gpu0", BudgetMb = 10_000 });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private (ServerInstance Server, ModelPreset Preset) Loaded(string name, int mb, ServerStatus status = ServerStatus.Running, string? group = "gpu0")
    {
        var server = new ServerInstance { Name = name, Status = status, MemoryGroup = group };
        _context.ServerInstances.Add(server);
        _context.SaveChanges();
        var preset = new ModelPreset { Name = name, ServerInstanceId = server.Id, ModelPath = "/x.gguf", MemoryEstimateMb = mb };
        _context.ModelPresets.Add(preset);
        _context.SaveChanges();
        server.ActivePresetId = preset.Id;
        _context.SaveChanges();
        return (server, preset);
    }

    private Task<MemoryGroupScheduler.Result> MakeRoom(ServerInstance starting, ModelPreset preset) =>
        new MemoryGroupScheduler(_context, _limiter, _ => null).MakeRoomAsync(starting, preset, (id, _) =>
        {
            _stopped.Add(id);
            _context.ServerInstances.Find(id)!.Status = ServerStatus.Idle;
            return Task.CompletedTask;
        }, CancellationToken.None);

    [Fact]
    public async Task Fits_NothingEvicted()
    {
        Loaded("a", 4000);
        var (target, preset) = Loaded("t", 5000, ServerStatus.Idle);

        using var result = await MakeRoom(target, preset);

        Assert.Empty(_stopped);
        Assert.True(result.Fits);
    }

    [Fact]
    public async Task EvictsLeastRecentlyUsedFirst_OnlyAsMuchAsNeeded()
    {
        var (older, _) = Loaded("older", 4000);
        var (newer, _) = Loaded("newer", 4000);
        _limiter.Touch(older.Id);
        await Task.Delay(5);
        _limiter.Touch(newer.Id);
        var (target, preset) = Loaded("t", 5000, ServerStatus.Idle);

        using var result = await MakeRoom(target, preset);

        Assert.Equal([older.Id], _stopped);
        Assert.True(result.Fits);
    }

    [Fact]
    public async Task BusyAndStartingServers_AreNeverEvicted()
    {
        var (busy, _) = Loaded("busy", 6000);
        Loaded("starting", 3000, ServerStatus.Starting);
        using var lease = _limiter.TryAcquire(busy.Id, 1);
        var (target, preset) = Loaded("t", 5000, ServerStatus.Idle);

        using var result = await MakeRoom(target, preset);

        Assert.Empty(_stopped);
        Assert.False(result.Fits);
    }

    [Fact]
    public async Task KeepLoadedPreset_IsNeverEvicted()
    {
        var (pinned, pinnedPreset) = Loaded("pinned", 4000);
        pinnedPreset.KeepLoaded = true;
        var (other, _) = Loaded("other", 4000);
        _limiter.Touch(other.Id); // more recent than "pinned", but "pinned" can't go
        _context.SaveChanges();
        var (target, preset) = Loaded("t", 5000, ServerStatus.Idle);

        using var result = await MakeRoom(target, preset);

        Assert.Equal([other.Id], _stopped);
        Assert.True(result.Fits);
    }

    [Fact]
    public async Task OtherGroupsAndOwnServer_AreIgnored()
    {
        Loaded("other-gpu", 9000, group: "gpu1");
        var (target, preset) = Loaded("t", 9000);   // running: a model swap on itself

        using var result = await MakeRoom(target, preset);

        Assert.Empty(_stopped);
        Assert.True(result.Fits);
    }

    [Fact]
    public async Task NoGroup_IsUnmanaged()
    {
        Loaded("a", 9000);
        var (target, preset) = Loaded("t", 9000, ServerStatus.Idle, group: null);

        using var result = await MakeRoom(target, preset);

        Assert.Empty(_stopped);
    }
}
