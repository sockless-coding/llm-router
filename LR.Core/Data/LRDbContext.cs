using Microsoft.EntityFrameworkCore;

namespace LR.Core.Data;

/// <summary>
/// EF Core DbContext for the LLM Router application.
/// Manages SQLite persistence for servers, presets, routing rules, and model statistics.
/// </summary>
public class LRDbContext : DbContext
{
    public DbSet<Models.ServerInstance> ServerInstances => Set<Models.ServerInstance>();
    public DbSet<Models.BackendConfig> BackendConfigs => Set<Models.BackendConfig>();
    public DbSet<Models.ModelPreset> ModelPresets => Set<Models.ModelPreset>();
    public DbSet<Models.RoutingRule> RoutingRules => Set<Models.RoutingRule>();
    public DbSet<Models.ModelStatistics> ModelStatistics => Set<Models.ModelStatistics>();

    public LRDbContext(DbContextOptions<LRDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ServerInstance configurations
        modelBuilder.Entity<Models.ServerInstance>(entity =>
        {
            entity.ToTable("ServerInstances");
            entity.HasIndex(e => e.Name).IsUnique();

            // Cascade delete: deleting a server cascades to its presets and stats
            entity.HasMany(s => s.Presets)
                .WithOne(p => p.ServerInstance)
                .HasForeignKey(p => p.ServerInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.ActivePreset)
                .WithMany()
                .HasForeignKey(s => s.ActivePresetId)
                .OnDelete(DeleteBehavior.SetNull);

            // One-to-one: ServerInstance -> BackendConfig (cascade delete)
            entity.HasOne(s => s.Config)
                .WithOne(c => c.ServerInstance)
                .HasForeignKey<Models.BackendConfig>(c => c.ServerInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // BackendConfig configurations
        modelBuilder.Entity<Models.BackendConfig>(entity =>
        {
            entity.ToTable("BackendConfigs");
            entity.HasIndex(e => e.ServerInstanceId).IsUnique();
            entity.Property(e => e.ExtraSettings).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v) ?? new Dictionary<string, string>());
        });

        // ModelPreset configurations
        modelBuilder.Entity<Models.ModelPreset>(entity =>
        {
            entity.ToTable("ModelPresets");
            entity.Property(e => e.Flags).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v),
                v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v) ?? new Dictionary<string, string>());
        });

        // RoutingRule configurations
        modelBuilder.Entity<Models.RoutingRule>(entity =>
        {
            entity.ToTable("RoutingRules");
            entity.HasOne(r => r.TargetServerInstance)
                .WithMany()
                .HasForeignKey(r => r.TargetServerInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ModelStatistics configurations
        modelBuilder.Entity<Models.ModelStatistics>(entity =>
        {
            entity.ToTable("ModelStatistics");

            // DateTimeOffset → TEXT converter for SQLite compatibility
            entity.Property(s => s.Timestamp).HasConversion(
                v => v.UtcDateTime.ToString("O"),
                v => DateTimeOffset.Parse(v));


            // DateTimeOffset → TEXT converter for SQLite compatibility
            entity.Property(s => s.Timestamp).HasConversion(
                v => v.UtcDateTime.ToString("O"),
                v => DateTimeOffset.Parse(v));

            entity.HasOne(s => s.ServerInstance)
                .WithMany()
                .HasForeignKey(s => s.ServerInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.Preset)
                .WithMany()
                .HasForeignKey(s => s.PresetId)
                .OnDelete(DeleteBehavior.SetNull);

            // Composite index for time-range queries per server
            entity.HasIndex(e => new { e.ServerInstanceId, e.Timestamp });

            // Index for preset-based context usage queries
            entity.HasIndex(e => new { e.PresetId, e.Timestamp });
        });
    }
}
