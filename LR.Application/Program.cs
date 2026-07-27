using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Providers;
using LR.Application.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor Pages with vertical slice structure
builder.Services.AddRazorPages();

// --- EF Core / SQLite persistence ---
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "lr.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";
builder.Services.AddDbContext<LRDbContext>(options => options.UseSqlite(connectionString));

// Core services (Scoped — need DbContext access per request)
builder.Services.AddScoped<IServerManager, ServerManager>();
builder.Services.AddScoped<IPresetManager, PresetManager>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddScoped<IRoutingEngine, RoutingEngine>();

// Backend provider factory (mock by default)
builder.Services.AddSingleton<IBackendProviderFactory>(sp =>
{
    var factory = new BackendProviderFactory();
    // Override with real providers here when ready:
    // factory.Register(BackendType.Cuda, () => new CudaLlamaCppProvider());
    return factory;
});

// Background services
builder.Services.AddHostedService<ServerHealthMonitorService>();

var app = builder.Build();

// Ensure database is created and migrations are applied on startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
    context.Database.Migrate();
}

app.UseStaticFiles();
app.MapRazorPages();

// Map stats API endpoints (Razor Pages return JSON via JsonResult)
// These are accessible at /api/stats/* routes

app.Run();
