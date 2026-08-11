using System.Net;
using System.Net.Http;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Providers;
using LR.Application.Services;
using LR.Application.Pages.Api;

var builder = WebApplication.CreateBuilder(args);

// Windows Service support — no-ops when launched standalone (console/dotnet run);
// when launched by the Service Control Manager it fixes the content root (which would
// otherwise default to C:\Windows\System32) and swaps in a service-aware lifetime.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "LLMRouter";
});

if (OperatingSystem.IsWindows())
{
    // Console logging isn't visible when running as a service, so also write to the
    // Windows Event Log.
    AddEventLogging(builder.Logging);
}

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

// API key authentication (Scoped — need DbContext for validation, and per-request state for scoping)
builder.Services.AddScoped<IApiKeyManager, ApiKeyManager>();
builder.Services.AddScoped<IApiKeyRequestContext, ApiKeyRequestContext>();

// GGUF metadata reader (Singleton — stateless file reader)
builder.Services.AddSingleton<IGgufMetadataReader, GgufMetadataReader>();

// Compute device inventory (Singleton — stateless WMI/pnputil wrapper). ComputeDeviceService
// checks OperatingSystem.IsWindows() itself at each call site, so it's safe to register
// unconditionally; the pragma silences the platform-compat analyzer's false positive on the
// constructor reference here.
#pragma warning disable CA1416
builder.Services.AddSingleton<IComputeDeviceService, ComputeDeviceService>();
#pragma warning restore CA1416

// SignalR progress publisher (bridges LR.Core and LR.Application)
builder.Services.AddScoped<LR.Core.Interfaces.ISignalRProgressPublisher, SignalRProgressPublisher>();

// SignalR stats publisher (bridges LR.Core and LR.Application)
builder.Services.AddScoped<LR.Core.Interfaces.IStatHubPublisher, StatHubPublisher>();

// --- Model library (registry + Hugging Face integration) ---
// Root folder / HF token are UI-controlled settings the app writes at runtime, so they're
// persisted to the DB (single-row table) rather than appsettings.json — see
// ModelLibrarySettingsService.
builder.Services.AddSingleton<IModelLibrarySettingsService, ModelLibrarySettingsService>();
builder.Services.AddScoped<IModelLibrary, ModelLibraryManager>();
builder.Services.AddHttpClient<IHuggingFaceClient, HuggingFaceClient>(client =>
{
    // Downloads can take a long time; cancellation is controlled by the caller's CancellationToken
    // instead of a fixed client timeout (see ModelDownloadService).
    client.Timeout = Timeout.InfiniteTimeSpan;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = DecompressionMethods.GZip,
});
builder.Services.AddSingleton<IModelDownloadProgressPublisher, ModelDownloadProgressPublisher>();
builder.Services.AddSingleton<ModelDownloadService>();

// Boot-time reconciliation for presets whose model file isn't in the registry yet
// (Scoped — needs DbContext; invoked explicitly below, not a BackgroundService).
builder.Services.AddScoped<ModelLibraryReconciliationService>();

// Logging & auto-restart services (Scoped — need DbContext access per request)
builder.Services.AddScoped<IServerLogService, LR.Core.Services.ServerLogService>();
builder.Services.AddScoped<IAutoRestartService, AutoRestartService>();

// Boot-time reconciliation for wrapper processes that outlived a previous router process
// (Scoped — needs DbContext; invoked explicitly below, not a BackgroundService).
builder.Services.AddScoped<LR.Application.Services.WrapperReconciliationService>();

// API Request Logger (Scoped — needs DbContext for reads/writes)
builder.Services.AddScoped<IApiRequestLogger, ApiRequestLogger>();

// API Request Logger (Scoped — needs DbContext for reads/writes)
builder.Services.AddScoped<IApiRequestLogger, ApiRequestLogger>();

// Responses API conversation-chain reconstruction (Scoped — needs DbContext) and the
// singleton registry of in-flight background responses' cancellation tokens.
builder.Services.AddScoped<ResponseChainBuilder>();
builder.Services.AddSingleton<IBackgroundResponseRegistry, BackgroundResponseRegistry>();

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

// The admin UI (Razor pages, SignalR hubs, static assets) always listens on Gateway:Port.
// Routing/protocol endpoints (/v1/*, Ollama /api/*) listen there too unless Gateway:RoutingPort
// is set to a different value, in which case they get their own listener — so the routing API
// can be exposed externally without also exposing the admin dashboard.
var routingPort = gatewaySettings.RoutingPort > 0 ? gatewaySettings.RoutingPort : gatewaySettings.Port;
var splitRoutingPort = routingPort != gatewaySettings.Port;

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

    if (splitRoutingPort && routingPort > 0)
    {
        options.ListenAnyIP(routingPort);
    }
});

// Request queue (singleton - holds the Channel)
builder.Services.AddSingleton<IRequestQueueService, RequestQueueService>();

// Protocol handlers (scoped — need DbContext access via preset manager)
builder.Services.AddScoped<LR.Application.Pages.Api.OpenAiHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.ClaudeHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.OllamaHandler>();
builder.Services.AddScoped<LR.Application.Pages.Api.ResponsesHandler>();

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

// Re-attach to any wrapper processes that outlived a previous router process before accepting
// requests, so the DB/UI reflect reality (not stale pre-restart state) from the first page load.
using (var scope = app.Services.CreateScope())
{
    var reconciliation = scope.ServiceProvider.GetRequiredService<LR.Application.Services.WrapperReconciliationService>();
    await reconciliation.ReconcileAsync();
}

// Register existing preset model files into the model library so it's populated without a
// manual import step on upgrade.
using (var scope = app.Services.CreateScope())
{
    var modelReconciliation = scope.ServiceProvider.GetRequiredService<ModelLibraryReconciliationService>();
    await modelReconciliation.ReconcileAsync();
}

// When the routing API has its own port, keep the two surfaces strictly separated: the admin
// port never serves routing/protocol traffic, and the routing port serves nothing else (so
// exposing it externally doesn't also expose the dashboard, SignalR hubs, or stats API). This
// checks the actual TCP connection port rather than the Host header, which a client could spoof.
if (splitRoutingPort)
{
    app.Use(async (context, next) =>
    {
        var isRoutingPort = context.Connection.LocalPort == routingPort;
        var isRoutingPath = IsRoutingApiPath(context.Request.Path);

        if (isRoutingPath != isRoutingPort && context.Request.Path != "/health")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
}

app.UseStaticFiles();
app.MapRazorPages();

// SignalR hub for server lifecycle events
app.MapHub<LR.Application.Hubs.ServerHub>("/serverHub");

// SignalR hub for model download progress
app.MapHub<LR.Application.Hubs.ModelDownloadHub>("/modelDownloadHub");

// SignalR hub for live inference statistics
app.MapHub<LR.Application.Hubs.StatsHub>("/statsHub");

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
    app.MapResponsesEndpoints();
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

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static void AddEventLogging(ILoggingBuilder logging)
{
    var settings = new Microsoft.Extensions.Logging.EventLog.EventLogSettings { SourceName = "LLM Router" };
    logging.AddEventLog(settings);
}

// Matches the protocol-compatible routing endpoints mapped in LR.Application.Pages.Api
// (OpenAI/Claude/Responses under /v1/*, Ollama under /api/*) — everything else (Razor pages,
// SignalR hubs, static assets, the /api/stats/* dashboard data endpoints) is "admin" traffic.
static bool IsRoutingApiPath(PathString path) =>
    path.StartsWithSegments("/v1") ||
    path.StartsWithSegments("/api/version") ||
    path.StartsWithSegments("/api/chat") ||
    path.StartsWithSegments("/api/tags") ||
    path.StartsWithSegments("/api/show") ||
    path.StartsWithSegments("/api/generate") ||
    path.StartsWithSegments("/api/embed") ||
    path.StartsWithSegments("/api/ps");
