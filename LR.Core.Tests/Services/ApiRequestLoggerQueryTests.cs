using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Core.Tests.Services;

/// <summary>
/// Guards the SQL-translatable Timestamp path in <see cref="ApiRequestLogger"/>. The
/// DateTimeOffset → round-trip-UTC-TEXT converter is what lets the "recent logs" and
/// retention queries filter/order in SQLite instead of pulling the whole table into
/// memory; if that converter is dropped these queries throw on evaluation.
/// </summary>
public class ApiRequestLoggerQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly ApiRequestLogger _logger;

    public ApiRequestLoggerQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<LRDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new LRDbContext(options);
        _context.Database.EnsureCreated();

        var settings = Options.Create(new GatewaySettings { EnableRequestLogging = true });
        _logger = new ApiRequestLogger(_context, NullLogger<ApiRequestLogger>.Instance, settings, new StubKeyContext());
    }

    private async Task SeedAsync(params DateTimeOffset[] timestamps)
    {
        foreach (var ts in timestamps)
        {
            _context.ApiRequestLogs.Add(new ApiRequestLog
            {
                Timestamp = ts,
                Protocol = ApiProtocol.OpenAI,
                EndpointPath = "/v1/chat/completions",
                IncomingPayload = new string('x', 4000),
                TranslatedPayload = new string('y', 4000),
            });
        }
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetRecentLogsAsync_ReturnsNewestFirst_WithinWindow()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(now.AddHours(-1), now.AddHours(-30), now.AddMinutes(-5), now.AddDays(-3));

        var (logs, total) = await _logger.GetRecentLogsAsync(2, from: now.AddDays(-1));

        Assert.Equal(2L, total); // only the two rows inside the last 24h
        Assert.Collection(logs,
            l => Assert.Equal(now.AddMinutes(-5), l.Timestamp, TimeSpan.FromSeconds(1)),
            l => Assert.Equal(now.AddHours(-1), l.Timestamp, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task GetRecentLogsAsync_NoWindow_OrdersAcrossWholeTable()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(now.AddDays(-10), now.AddMinutes(-1), now.AddDays(-2));

        var (logs, total) = await _logger.GetRecentLogsAsync(10);

        Assert.Equal(3L, total);
        Assert.Equal(now.AddMinutes(-1), logs[0].Timestamp, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddDays(-10), logs[^1].Timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DeleteOlderThanAsync_RemovesOnlyRowsBeforeCutoff()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(now.AddDays(-9), now.AddDays(-8), now.AddHours(-1));

        var deleted = await _logger.DeleteOlderThanAsync(now.AddDays(-7));

        Assert.Equal(2L, deleted);
        Assert.Equal(1, await _context.ApiRequestLogs.CountAsync());
    }

    [Fact]
    public async Task GetSummaryStatsAsync_CountsTodayByProtocol()
    {
        var now = DateTimeOffset.UtcNow;
        var earlierToday = new DateTimeOffset(now.UtcDateTime.Date.AddHours(1), TimeSpan.Zero);
        _context.ApiRequestLogs.AddRange(
            new ApiRequestLog { Timestamp = now, Protocol = ApiProtocol.OpenAI, EndpointPath = "/", IncomingPayload = "", TotalLatencyMs = 100 },
            new ApiRequestLog { Timestamp = earlierToday, Protocol = ApiProtocol.Claude, EndpointPath = "/", IncomingPayload = "", TotalLatencyMs = 300 },
            new ApiRequestLog { Timestamp = now.AddDays(-2), Protocol = ApiProtocol.OpenAI, EndpointPath = "/", IncomingPayload = "" });
        await _context.SaveChangesAsync();

        var stats = await _logger.GetSummaryStatsAsync();

        Assert.Equal(2L, stats.TotalToday);
        Assert.Equal(1L, stats.OpenAIToday);
        Assert.Equal(1L, stats.ClaudeToday);
        Assert.Equal(200d, stats.AvgLatencyMs);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private sealed class StubKeyContext : IApiKeyRequestContext
    {
        public ApiKey? CurrentKey { get; set; }
        public bool IsModelAllowed(Guid presetId) => true;
        public IEnumerable<ModelPreset> FilterAllowed(IEnumerable<ModelPreset> presets) => presets;
    }
}
