using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class ApiKeyUsageServiceTests : IDisposable
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly ApiKeyUsageService _service;

    public ApiKeyUsageServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _service = new ApiKeyUsageService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<ApiKey> AddKeyAsync(string name)
    {
        var key = new ApiKey { Name = name, KeyHash = Guid.NewGuid().ToString("N"), KeyPrefix = "lr-" + name[..Math.Min(4, name.Length)] };
        _context.ApiKeys.Add(key);
        await _context.SaveChangesAsync();
        return key;
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso);

    [Fact]
    public async Task RecordAsync_AccumulatesWithinHour_AndSplitsByModelHourAndKey()
    {
        var key = await AddKeyAsync("alpha");
        await _service.RecordAsync(key.Id, "llama", null, 100, 10, At("2026-11-14T10:05:00Z"));
        await _service.RecordAsync(key.Id, "llama", null, 50, 5, At("2026-11-14T10:59:59Z"));
        await _service.RecordAsync(key.Id, "qwen", null, 7, 3, At("2026-11-14T10:30:00Z"));
        await _service.RecordAsync(key.Id, "llama", null, 1, 1, At("2026-11-14T11:00:00Z"));
        await _service.RecordAsync(null, "llama", null, 2, 2, At("2026-11-14T10:10:00Z"));

        var rows = await _context.ApiKeyUsageHourly.AsNoTracking().ToListAsync();
        Assert.Equal(4, rows.Count);

        var llama10 = Assert.Single(rows, r => r.ApiKeyId == key.Id && r.ModelName == "llama" && r.HourStart == At("2026-11-14T10:00:00Z"));
        Assert.Equal(2, llama10.RequestCount);
        Assert.Equal(150, llama10.PromptTokens);
        Assert.Equal(15, llama10.GeneratedTokens);
        Assert.Equal("alpha", llama10.ApiKeyName);
        Assert.Equal(key.KeyPrefix, llama10.ApiKeyPrefix);

        var keyless = Assert.Single(rows, r => r.ApiKeyId == Guid.Empty);
        Assert.Equal(ApiKeyUsageService.NoKeyName, keyless.ApiKeyName);
    }

    [Fact]
    public async Task RecordAsync_RawSqlGuids_MatchEfQueries()
    {
        // The upsert binds Guids as raw SQL parameters; LINQ filters must still find the rows.
        var key = await AddKeyAsync("beta");
        var presetId = Guid.NewGuid();
        await _service.RecordAsync(key.Id, "m", presetId, 1, 1, At("2026-11-14T10:00:00Z"));

        var row = await _context.ApiKeyUsageHourly.AsNoTracking().SingleAsync(r => r.ApiKeyId == key.Id && r.PresetId == presetId);
        Assert.Equal("m", row.ModelName);
    }

    [Fact]
    public async Task GetReportAsync_BucketsByDayWeekMonthYear()
    {
        var key = await AddKeyAsync("alpha");
        // Wed 2026-11-04, Sun 2026-11-08 (same ISO week), Mon 2026-11-09 (next week), Dec 1.
        await _service.RecordAsync(key.Id, "llama", null, 10, 1, At("2026-11-04T12:00:00Z"));
        await _service.RecordAsync(key.Id, "llama", null, 20, 2, At("2026-11-08T12:00:00Z"));
        await _service.RecordAsync(key.Id, "llama", null, 30, 3, At("2026-11-09T12:00:00Z"));
        await _service.RecordAsync(key.Id, "llama", null, 40, 4, At("2026-12-01T12:00:00Z"));

        var from = new DateOnly(2026, 11, 1);
        var to = new DateOnly(2026, 12, 31);

        var days = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, from, to, Utc));
        Assert.Equal(4, days.Count);

        var weeks = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Week, from, to, Utc));
        Assert.Equal(new[] { new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 30) }, weeks.Select(w => w.PeriodStart));
        Assert.Equal(33, weeks[0].TotalTokens); // 10+1 + 20+2
        Assert.Equal(2, weeks[0].RequestCount);

        var months = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Month, from, to, Utc));
        Assert.Equal(2, months.Count);
        Assert.Equal(3, months[0].RequestCount);
        Assert.Equal(66, months[0].TotalTokens);

        var years = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Year, from, to, Utc));
        var year = Assert.Single(years);
        Assert.Equal(new DateOnly(2026, 1, 1), year.PeriodStart);
        Assert.Equal(4, year.RequestCount);
        Assert.Equal(110, year.TotalTokens);

        var total = Assert.Single(await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Total, from, to, Utc)));
        Assert.Equal(from, total.PeriodStart);
    }

    [Fact]
    public async Task GetReportAsync_UsesTimeZoneForDayBoundariesAndRange()
    {
        var key = await AddKeyAsync("alpha");
        // 23:30 UTC on Nov 14 is 01:30 on Nov 15 at UTC+2.
        await _service.RecordAsync(key.Id, "llama", null, 5, 5, At("2026-11-14T23:30:00Z"));

        var utcDays = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 14), Utc));
        Assert.Equal(new DateOnly(2026, 11, 14), Assert.Single(utcDays).PeriodStart);

        var plus2Same = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 14), Plus2));
        Assert.Empty(plus2Same);

        var plus2Next = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15), Plus2));
        Assert.Equal(new DateOnly(2026, 11, 15), Assert.Single(plus2Next).PeriodStart);
    }

    [Fact]
    public async Task GetReportAsync_FiltersByKeyAndModel_AndNamesDeletedAndRenamedKeys()
    {
        var alpha = await AddKeyAsync("alpha");
        var beta = await AddKeyAsync("beta");
        var at = At("2026-11-14T10:00:00Z");
        await _service.RecordAsync(alpha.Id, "llama", null, 1, 1, at);
        await _service.RecordAsync(alpha.Id, "qwen", null, 1, 1, at);
        await _service.RecordAsync(beta.Id, "llama", null, 1, 1, at);
        await _service.RecordAsync(null, "llama", null, 1, 1, at);

        alpha.Name = "alpha-renamed";
        _context.ApiKeys.Remove(beta);
        await _context.SaveChangesAsync();

        var day = new DateOnly(2026, 11, 14);
        var all = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, day, day, Utc));
        Assert.Equal(4, all.Count);
        Assert.All(all.Where(r => r.ApiKeyId == alpha.Id), r => Assert.Equal("alpha-renamed", r.ApiKeyName));
        var betaRow = Assert.Single(all, r => r.ApiKeyId == beta.Id);
        Assert.True(betaRow.KeyDeleted);
        Assert.Equal("beta", betaRow.ApiKeyName);

        var alphaOnly = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, day, day, Utc, ApiKeyId: alpha.Id));
        Assert.Equal(2, alphaOnly.Count);

        var keyless = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, day, day, Utc, ApiKeyId: Guid.Empty));
        Assert.Equal(ApiKeyUsageService.NoKeyName, Assert.Single(keyless).ApiKeyName);

        var llama = await _service.GetReportAsync(new UsageReportQuery(UsagePeriod.Day, day, day, Utc, ModelName: "llama"));
        Assert.Equal(3, llama.Count);
    }

    [Theory]
    [InlineData("2026-11-09", "2026-11-09")] // Monday
    [InlineData("2026-11-11", "2026-11-09")] // Wednesday
    [InlineData("2026-11-15", "2026-11-09")] // Sunday belongs to the week starting the previous Monday
    public void PeriodStart_Week_IsIsoMonday(string date, string expected)
    {
        var start = ApiKeyUsageService.PeriodStart(DateOnly.Parse(date), UsagePeriod.Week, default);
        Assert.Equal(DateOnly.Parse(expected), start);
    }

    [Fact]
    public void FormatHour_MatchesDbConverterFormat()
    {
        Assert.Equal("2026-11-14T10:00:00.0000000Z", ApiKeyUsageService.FormatHour(ApiKeyUsageService.HourStartUtc(At("2026-11-14T12:34:56+02:00"))));
    }
}

/// <summary>
/// Runs the real migrations to check the backfill SQL in AddApiKeyUsageHourly rolls existing
/// ModelStatistics history up the same way <see cref="ApiKeyUsageService.RecordAsync"/> would.
/// </summary>
public class ApiKeyUsageBackfillMigrationTests
{
    [Fact]
    public async Task Migration_BackfillsHourlyUsageFromModelStatistics()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(connection).Options);

        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToList();
        var target = migrations.Single(m => m.EndsWith("_AddApiKeyUsageHourly"));
        await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1]);

        // Seed rows the way EF wrote them before this migration; FK checks off to skip the
        // server/preset graph that isn't relevant here.
        var keyId = Guid.NewGuid();
        var presetId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await context.Database.ExecuteSqlAsync($"INSERT INTO ApiKeys (Id, Name, KeyHash, KeyPrefix, IsEnabled, AllowAllModels, CreatedAt) VALUES ({keyId}, 'alpha', 'h', 'lr-alph', 1, 1, '2026-11-01T00:00:00.0000000+00:00')");
        var preset = new ModelPreset { Id = presetId, Name = "llama" };
        context.ModelPresets.Add(preset);
        await context.SaveChangesAsync();
        foreach (var (ts, key, p, gen) in new (string, Guid?, int, int)[]
        {
            ("2026-11-14T10:05:00.0000000Z", keyId, 100, 10),
            ("2026-11-14T10:45:00.0000000Z", keyId, 50, 5),
            ("2026-11-14T11:01:00.0000000Z", keyId, 1, 1),
            ("2026-11-14T10:20:00.0000000Z", null, 7, 7),
        })
        {
            await context.Database.ExecuteSqlAsync($@"INSERT INTO ModelStatistics
                (Id, ServerInstanceId, PresetId, ApiKeyId, Timestamp, PromptTokensProcessed, PromptProcessingMs, PromptTokensPerSecReported,
                 GeneratedTokenCount, GenerationMs, TotalLatencyMs, FirstTokenLatencyMs, ContextLengthUsed, ContextMaxLength, DraftAccepted, DraftGenerated)
                VALUES ({Guid.NewGuid()}, {serverId}, {presetId}, {key}, {ts}, {p}, 0, 0, {gen}, 0, 0, 0, 0, 0, 0, 0)");
        }

        await migrator.MigrateAsync(target);

        var rows = await context.ApiKeyUsageHourly.AsNoTracking().OrderBy(r => r.HourStart).ToListAsync();
        Assert.Equal(3, rows.Count);
        var first = Assert.Single(rows, r => r.ApiKeyId == keyId && r.HourStart == DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        Assert.Equal(2, first.RequestCount);
        Assert.Equal(150, first.PromptTokens);
        Assert.Equal(15, first.GeneratedTokens);
        Assert.Equal("llama", first.ModelName);
        Assert.Equal("alpha", first.ApiKeyName);
        Assert.Equal("lr-alph", first.ApiKeyPrefix);
        Assert.Equal(presetId, first.PresetId);
        Assert.Equal(ApiKeyUsageService.NoKeyName, Assert.Single(rows, r => r.ApiKeyId == Guid.Empty).ApiKeyName);

        // The backfilled rows must accept further upserts on the same primary key.
        await new ApiKeyUsageService(context).RecordAsync(keyId, "llama", presetId, 1, 1, DateTimeOffset.Parse("2026-11-14T10:50:00Z"));
        var updated = await context.ApiKeyUsageHourly.AsNoTracking().SingleAsync(r => r.ApiKeyId == keyId && r.HourStart == DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        Assert.Equal(3, updated.RequestCount);
    }
}
