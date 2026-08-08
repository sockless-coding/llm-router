using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Providers;
using LR.Application.Services;
using LR.Application.Pages.Api;

var builder = WebApplication.CreateBuilder(args);

// Razor Pages with vertical slice structure
builder.Services.AddRazorPages();

// SignalR for real-time server lifecycle events
builder.Services.AddSignalR();

// --- EF Core / SQLite persistence ---
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "lr.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var connectionString = $"Data Source={dbPath}";
builder.Services.AddDbContext<LRDbContext>(options => options.UseSqlite(connectionString));

// Provider registry (Singleton — holds runtime IBackendProvider references across scopes)
builder.Services.AddSingleton<ProviderRegistry>();

// Core services (Scoped — need DbContext access per request)
builder.Services.AddScoped<IServerManager, ServerManager>();
builder.Services.AddScoped<IPresetManager, PresetManager>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddScoped<IRoutingEngine, RoutingEngine>();

// GGUF metadata reader (Singleton — stateless file reader)
builder.Services.AddSingleton<IGgufMetadataReader, GgufMetadataReader>();

// SignalR progress publisher (bridges LR.Core and LR.Application)
builder.Services.AddScoped<LR.Core.Interfaces.ISignalRProgressPublisher, SignalRProgressPublisher>();

// Logging & auto-restart services (Scoped — need DbContext access per request)
builder.Services.AddScoped<IServerLogService, LR.Core.Services.ServerLogService>();
builder.Services.AddScoped<IAutoRestartService, AutoRestartService>();

// API Request Logger (Scoped — needs DbContext for reads/writes)
builder.Services.AddScoped<IApiRequestLogger, ApiRequestLogger>();

// API Request Logger (Scoped — needs DbContext for reads/writes)
builder.Services.AddScoped<IApiRequestLogger, ApiRequestLogger>();

// Backend provider factory (mock by default)
builder.Services.AddSingleton<IBackendProviderFactory>(sp =>
{
    var factory = new BackendProviderFactory(sp);
    // Override with real providers here when ready:
    // factory.Register(ServerEngine.LlamaCpp, () => new RealLlamaCppProvider());
    return factory;
});

// --- Gateway configuration ---
var gatewaySettings = builder.Configuration.GetSection("Gateway").Get<GatewaySettings>() ?? new GatewaySettings();
builder.Services.AddSingleton(gatewaySettings);
builder.Services.Configure<GatewaySettings>(builder.Configuration.GetSection("Gateway"));

// Configure Kestrel for long-running inference requests
builder.WebHost.ConfigureKestrel(options =>
{
    // Keep alive settings for long SSE/streaming connections
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);

    if (gatewaySettings.Port > 0)
    {
        options.ListenAnyIP(gatewaySettings.Port);
    }
});

// Request queue (singleton - holds the Channel)
builder.Services.AddSingleton<IRequestQueueService, RequestQueueService>();

// Protocol handlers (scoped — need DbContext access via preset manager)
builder.Services.AddScoped<LR.Application.Pages.Api.OpenAiHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.ClaudeHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.OllamaHandler>();

// Background services
builder.Services.AddHostedService<ServerHealthMonitorService>();
builder.Services.AddHostedService<LR.Application.Services.RequestDispatcherService>();

// Retention cleanup for request logs (runs hourly)
builder.Services.AddHostedService<RetentionCleanupService>();

// Retention cleanup for request logs (runs hourly)
builder.Services.AddHostedService<RetentionCleanupService>();

var app = builder.Build();

// Ensure database is created and migrations are applied on startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
    context.Database.Migrate();
}

app.UseStaticFiles();
app.MapRazorPages();

// SignalR hub for server lifecycle events
app.MapHub<LR.Application.Hubs.ServerHub>("/serverHub");

// --- Health endpoint for agents ---
app.MapGet("/health", async (IServerManager serverManager) =>
{
    return Results.Json(new
    {
        status = "ok"
    });
    var instances = await serverManager.GetAllInstancesAsync();
    var hasHealthyServer = instances.Any(s => s.Status == ServerStatus.Running && s.IsHealthy);

    string status;
    if (!instances.Any())
        status = "degraded";
    else if (hasHealthyServer)
        status = "healthy";
    else
        status = "unhealthy";

    return Results.Json(new
    {
        gateway = "up",
        status,
        servers = instances.Select(s => new
        {
            s.Name,
            s.Status,
            healthy = s.IsHealthy
        })
    });
});

// Map stats API endpoints (Razor Pages return JSON via JsonResult)
// These are accessible at /api/stats/* routes

// --- Protocol API Endpoints ---
var enabledProtocols = gatewaySettings.EnabledProtocols.Length > 0
    ? new HashSet<ApiProtocol>(gatewaySettings.EnabledProtocols)
    : new HashSet<ApiProtocol> { ApiProtocol.OpenAI, ApiProtocol.Claude, ApiProtocol.Ollama };

if (enabledProtocols.Contains(ApiProtocol.OpenAI))
{
    app.MapOpenAiEndpoints();
}

if (enabledProtocols.Contains(ApiProtocol.Claude))
{
    app.MapClaudeEndpoints();
}

if (enabledProtocols.Contains(ApiProtocol.Ollama))
{
    app.MapOllamaEndpoints();
}

app.Run();
