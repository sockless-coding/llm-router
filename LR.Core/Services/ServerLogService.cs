using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LR.Core.Services;

/// <summary>
/// Service for logging and retrieving server log entries from the database.
/// </summary>
public class ServerLogService : IServerLogService
{
    private readonly LRDbContext _context;

    public ServerLogService(LRDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task LogAsync(ServerInstance instance, ServerLogLevel level, string message)
    {
        var logEntry = new ServerLog
        {
            ServerInstanceId = instance.Id,
            Timestamp = DateTime.UtcNow,
            Level = ((int)level).ToString(),
            Message = message
        };

        _context.Set<ServerLog>().Add(logEntry);
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<List<ServerLog>> GetLogsAsync(Guid serverInstanceId, int count = 100)
    {
        return await _context.Set<ServerLog>()
            .Where(l => l.ServerInstanceId == serverInstanceId)
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task ClearLogsAsync(Guid serverInstanceId)
    {
        var logs = await _context.Set<ServerLog>()
            .Where(l => l.ServerInstanceId == serverInstanceId)
            .ToListAsync();

        _context.Set<ServerLog>().RemoveRange(logs);
        await _context.SaveChangesAsync();
    }
}
