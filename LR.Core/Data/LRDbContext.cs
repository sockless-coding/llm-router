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
    public DbSet<Models.ServerLog> ServerLogs => Set<Models.ServerLog>();
    public DbSet<Models.ApiRequestLog> ApiRequestLogs => Set<Models.ApiRequestLog>();
    public DbSet<Models.StoredResponse> StoredResponses => Set<Models.StoredResponse>();
    public DbSet<Models.LocalModel> LocalModels => Set<Models.LocalModel>();
    public DbSet<Models.ModelLibrarySettings> ModelLibrarySettings => Set<Models.ModelLibrarySettings>();
    public DbSet<Models.ApiKey> ApiKeys => Set<Models.ApiKey>();
    public DbSet<Models.ApiKeyModelPreset> ApiKeyModelPresets => Set<Models.ApiKeyModelPreset>();

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

            // Cascade delete: deleting a server cascades to its logs
            entity.HasMany(s => s.Logs)
                .WithOne(l => l.ServerInstance)
                .HasForeignKey(l => l.ServerInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            // Cascade delete: deleting a server cascades to its logs
            entity.HasMany(s => s.Logs)
                .WithOne(l => l.ServerInstance)
                .HasForeignKey(l => l.ServerInstanceId)
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

            // Optional link to the model registry — a deleted model just clears the link,
            // the preset's own ModelPath keeps working.
            entity.HasOne(p => p.Model)
                .WithMany(m => m.Presets)
                .HasForeignKey(p => p.ModelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // LocalModel configurations
        modelBuilder.Entity<Models.LocalModel>(entity =>
        {
            entity.ToTable("LocalModels");
            entity.HasIndex(e => e.FilePath).IsUnique();
        });

        // ModelLibrarySettings configurations
        modelBuilder.Entity<Models.ModelLibrarySettings>(entity =>
        {
            entity.ToTable("ModelLibrarySettings");
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

        // ServerLog configurations
        modelBuilder.Entity<Models.ServerLog>(entity =>
        {
            entity.ToTable("ServerLogs");
            entity.HasIndex(e => new { e.ServerInstanceId, e.Timestamp });
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

            // Optional FK to ApiKey — stats survive key deletion
            entity.HasOne(s => s.ApiKey)
                .WithMany()
                .HasForeignKey(s => s.ApiKeyId)
                .OnDelete(DeleteBehavior.SetNull);

            // Composite index for time-range queries per server
            entity.HasIndex(e => new { e.ServerInstanceId, e.Timestamp });

            // Index for per-key usage aggregation over a time range
            entity.HasIndex(e => new { e.ApiKeyId, e.Timestamp });

            // Index for preset-based context usage queries
            entity.HasIndex(e => new { e.PresetId, e.Timestamp });
        });

        // ApiRequestLog configurations
        modelBuilder.Entity<Models.ApiRequestLog>(entity =>
        {
            entity.ToTable("ApiRequestLogs");

            // Optional FK to ServerInstance — logs survive server deletion
            entity.HasOne(l => l.ServerInstance)
                .WithMany()
                .HasForeignKey(l => l.ServerInstanceId)
                .OnDelete(DeleteBehavior.SetNull);

            // Optional FK to ModelPreset — logs survive preset deletion
            entity.HasOne(l => l.Preset)
                .WithMany()
                .HasForeignKey(l => l.PresetId)
                .OnDelete(DeleteBehavior.SetNull);

            // Optional FK to ApiKey — logs survive key deletion
            entity.HasOne(l => l.ApiKey)
                .WithMany()
                .HasForeignKey(l => l.ApiKeyId)
                .OnDelete(DeleteBehavior.SetNull);

            // Index for retention cleanup queries (delete old records by timestamp)
            entity.HasIndex(e => e.Timestamp);

            // Composite index for filtering by protocol + time range
            entity.HasIndex(e => new { e.Protocol, e.Timestamp });
        });

        // StoredResponse configurations (OpenAI Responses API conversation state)
        modelBuilder.Entity<Models.StoredResponse>(entity =>
        {
            entity.ToTable("StoredResponses");
            entity.HasKey(e => e.Id);

            // Used to walk previous_response_id chains and for retention cleanup.
            entity.HasIndex(e => e.PreviousResponseId);
            entity.HasIndex(e => e.CreatedAt);
        });

        // ApiKey configurations
        modelBuilder.Entity<Models.ApiKey>(entity =>
        {
            entity.ToTable("ApiKeys");
            entity.HasIndex(e => e.KeyHash).IsUnique();
        });

        // ApiKeyModelPreset configurations (many-to-many join: key <-> allowed preset)
        modelBuilder.Entity<Models.ApiKeyModelPreset>(entity =>
        {
            entity.ToTable("ApiKeyModelPresets");
            entity.HasKey(e => new { e.ApiKeyId, e.ModelPresetId });

            // Deleting a key drops its scoping rows; deleting a preset drops any rows that
            // reference it (the key itself keeps existing, just with one less allowed model).
            entity.HasOne(e => e.ApiKey)
                .WithMany(k => k.AllowedPresets)
                .HasForeignKey(e => e.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ModelPreset)
                .WithMany()
                .HasForeignKey(e => e.ModelPresetId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
