using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Providers;
using LR.Application.Services;

var builder = WebApplication.CreateBuilder(args);

// Razor Pages with vertical slice structure
builder.Services.AddRazorPages();

// Core services
builder.Services.AddSingleton<IServerManager, ServerManager>();
builder.Services.AddSingleton<IPresetManager, PresetManager>();
builder.Services.AddSingleton<IRoutingEngine, RoutingEngine>();

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

app.UseStaticFiles();
app.MapRazorPages();

app.Run();
