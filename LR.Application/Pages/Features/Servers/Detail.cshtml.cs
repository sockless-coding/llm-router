using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class ServerDetailModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly IServerLogService _logService;
    private readonly IServerConcurrencyLimiter _concurrencyLimiter;
    private readonly IPresetManager _presetManager;
    private readonly GatewaySettings _settings;

    public Core.Models.ServerInstance? Server { get; set; }
    public IReadOnlyList<Core.Models.ServerLog> Logs { get; set; } = new List<Core.Models.ServerLog>();

    /// <summary>Wrapper process ID, if a wrapper is currently connected for this server. Diagnostics only.</summary>
    public int? WrapperPid { get; set; }

    /// <summary>Managed server process ID, if currently running. Diagnostics only.</summary>
    public int? ServerPid { get; set; }

    /// <summary>Live runtime facts read from the running llama.cpp server's /props, if available.</summary>
    public Core.Models.LlamaServerProps? ServerProps { get; set; }

    /// <summary>Live KV-cache usage read from the running llama.cpp server's /metrics, if available.</summary>
    public Core.Models.LlamaRuntimeUsage? RuntimeUsage { get; set; }

    /// <summary>Requests the router currently has in flight to this server.</summary>
    public int InFlight { get; set; }

    /// <summary>Parallel-request capacity the router applies to this server (0 if not applicable).</summary>
    public int MaxSlots { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public ServerDetailModel(
        IServerManager serverManager,
        IServerLogService logService,
        IServerConcurrencyLimiter concurrencyLimiter,
        IPresetManager presetManager,
        GatewaySettings settings)
    {
        _serverManager = serverManager;
        _logService = logService;
        _concurrencyLimiter = concurrencyLimiter;
        _presetManager = presetManager;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        var instances = await _serverManager.GetAllInstancesAsync();
        Server = instances.FirstOrDefault(s => s.Id == Id);

        if (Server != null)
        {
            Logs = await _logService.GetLogsAsync(Server.Id, 200);

            var provider = _serverManager.GetProvider(Server.Id);

            if (provider is IWrapperDiagnostics diagnostics)
            {
                WrapperPid = diagnostics.WrapperPid;
                ServerPid = diagnostics.ServerPid;
            }

            if (provider is IServerCapacityProvider capacity)
            {
                ServerProps = capacity.ServerProps;
                RuntimeUsage = capacity.RuntimeUsage;
            }

            InFlight = _concurrencyLimiter.InFlight(Server.Id);

            if (Server.Engine == ServerEngine.LlamaCpp && Server.Status == ServerStatus.Running)
            {
                var preset = Server.ActivePresetId is Guid pid ? _presetManager.GetById(pid) : null;
                MaxSlots = LlamaSlotCapacity.Resolve(provider, preset, _settings.DefaultParallelSlots);
            }
        }
    }

    public async Task<JsonResult> OnPostClearLogsAsync()
    {
        if (!Id.Equals(Guid.Empty))
        {
            await _logService.ClearLogsAsync(Id);
        }
        return new JsonResult(new { success = true });
    }
}
