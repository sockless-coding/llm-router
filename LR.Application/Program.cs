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
    // factory.Register(ServerEngine.LlamaCpp, () => new RealLlamaCppProvider());
    return factory;
});

// --- Gateway configuration ---
var gatewaySettings = builder.Configuration.GetSection("Gateway").Get<GatewaySettings>() ?? new GatewaySettings();
builder.Services.AddSingleton(gatewaySettings);
builder.Services.Configure<GatewaySettings>(builder.Configuration.GetSection("Gateway"));

// Configure Kestrel to listen on the gateway port
if (gatewaySettings.Port > 0)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(gatewaySettings.Port);
    });
}

// Request queue (singleton - holds the Channel)
builder.Services.AddSingleton<IRequestQueueService, RequestQueueService>();

// Protocol handlers (scoped — need DbContext access via preset manager)
builder.Services.AddScoped<LR.Application.Pages.Api.OpenAiHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.ClaudeHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.OllamaHandler>();

// Background services
builder.Services.AddHostedService<ServerHealthMonitorService>();
builder.Services.AddHostedService<LR.Application.Services.RequestDispatcherService>();

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

// --- Protocol API Endpoints ---
// Determine which protocols to enable (empty = all enabled)
var enabledProtocols = gatewaySettings.EnabledProtocols.Length > 0
    ? new HashSet<ApiProtocol>(gatewaySettings.EnabledProtocols)
    : new HashSet<ApiProtocol> { ApiProtocol.OpenAI, ApiProtocol.Claude, ApiProtocol.Ollama };

// OpenAI-compatible endpoints: POST /v1/chat/completions, GET /v1/models
if (enabledProtocols.Contains(ApiProtocol.OpenAI))
{
    app.MapPost("/v1/chat/completions", async (
            LR.Application.Pages.Api.OpenAiHandler handler,
            HttpRequest httpRequest,
            HttpResponse httpResponse,
            CancellationToken ct) =>
        {
            return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
        });

    app.MapGet("/v1/models", async (
            LR.Application.Pages.Api.OpenAiHandler handler) =>
        {
            var result = await handler.HandleListModelsAsync();
            return Microsoft.AspNetCore.Http.Results.Json(result);
        });
}

// Claude-compatible endpoints: POST /v1/messages
if (enabledProtocols.Contains(ApiProtocol.Claude))
{
    app.MapPost("/v1/messages", async (
            LR.Application.Pages.Api.ClaudeHandler handler,
            HttpRequest httpRequest,
            HttpResponse httpResponse,
            CancellationToken ct) =>
        {
            return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
        });
}

// Ollama-compatible endpoints: POST /api/chat, GET /api/tags
if (enabledProtocols.Contains(ApiProtocol.Ollama))
{
    app.MapPost("/api/chat", async (
            LR.Application.Pages.Api.OllamaHandler handler,
            HttpRequest httpRequest,
            HttpResponse httpResponse,
            CancellationToken ct) =>
        {
            return await handler.HandleChatCompletionAsync(httpRequest, httpResponse, ct);
        });

    app.MapGet("/api/tags", async (
            LR.Application.Pages.Api.OllamaHandler handler) =>
        {
            var result = await handler.HandleListModelsAsync();
            return Microsoft.AspNetCore.Http.Results.Json(result);
        });
}

app.Run();
