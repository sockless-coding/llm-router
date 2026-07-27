using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LR.Core.Data;

/// <summary>
/// Design-time factory for EF Core migrations.
/// Provides a DbContext instance without requiring the full DI container.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LRDbContext>
{
    public LRDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LRDbContext>();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "lr.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connectionString = $"Data Source={dbPath}";
        optionsBuilder.UseSqlite(connectionString);

        return new LRDbContext(optionsBuilder.Options);
    }
}
