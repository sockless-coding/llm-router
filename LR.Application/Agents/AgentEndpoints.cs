using System.Net;

using Microsoft.EntityFrameworkCore;

using LR.Core.Agents;
using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Agents;

/// <summary>
/// The HTTP surface agents (and their install scripts) use. Everything here authenticates with
/// either the enrollment token (first contact, package download during install) or an agent's own
/// id + secret — never the admin UI's lack of auth.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapGet(AgentProtocol.InstallShPath, async (HttpRequest request, AgentSettingsService settings) =>
        {
            var url = AgentSettingsService.ResolveRouterUrl(await settings.GetAsync(), request);
            return Results.Text(AgentInstallScripts.LinuxScript(url), "text/x-shellscript");
        });

        app.MapGet(AgentProtocol.InstallPs1Path, async (HttpRequest request, AgentSettingsService settings) =>
        {
            var url = AgentSettingsService.ResolveRouterUrl(await settings.GetAsync(), request);
            return Results.Text(AgentInstallScripts.WindowsScript(url), "text/plain");
        });

        app.MapPost(AgentProtocol.EnrollPath, EnrollAsync);
        app.Map(AgentProtocol.ConnectPath, ConnectAsync);

        app.MapGet(AgentProtocol.PackagePath + "/{rid}", async (string rid, HttpContext http, AgentPackageCatalog packages, AgentSettingsService settings, LRDbContext context) =>
        {
            // Install scripts download with the enrollment token; agents updating use their own secret.
            var bearer = BearerToken(http.Request);
            var enrollment = (await settings.GetAsync()).EnrollmentToken;
            if (!AgentSecrets.Matches(bearer, enrollment) && await AuthenticateAgentAsync(http.Request, context) is not { IsEnabled: true })
                return Results.Unauthorized();

            if (packages.Find(rid) is not { } package)
                return Results.NotFound($"No agent package for {rid} on this router.");
            return Results.File(package.Path, "application/octet-stream", Path.GetFileName(package.Path), enableRangeProcessing: true);
        });

        app.MapGet(AgentProtocol.ModelFilePath + "/{modelId:guid}/{index:int}", async (Guid modelId, int index, HttpContext http, LRDbContext context) =>
        {
            if (await AuthenticateAgentAsync(http.Request, context) is not { IsEnabled: true })
                return Results.Unauthorized();

            var model = await context.LocalModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId && m.AgentId == null);
            if (model is null) return Results.NotFound();
            var shards = SplitGguf.AllShards(model.FilePath);
            if (index < 0 || index >= shards.Count || !File.Exists(shards[index])) return Results.NotFound();
            return Results.File(shards[index], "application/octet-stream", Path.GetFileName(shards[index]), enableRangeProcessing: true);
        });
    }

    private static async Task<IResult> EnrollAsync(AgentEnrollRequest body, AgentSettingsService settings, LRDbContext context, AgentConnectionManager connections)
    {
        var current = await settings.GetAsync();
        if (!AgentSecrets.Matches(body.EnrollmentToken, current.EnrollmentToken))
            return Results.Unauthorized();

        var secret = AgentSecrets.Generate("lra_");
        RemoteAgent? agent = null;
        if (!string.IsNullOrWhiteSpace(body.MachineId))
            agent = await context.RemoteAgents.FirstOrDefaultAsync(a => a.MachineId == body.MachineId);

        if (agent is null)
        {
            var baseName = string.IsNullOrWhiteSpace(body.Hostname) ? "agent" : body.Hostname.Trim();
            var name = baseName;
            for (int i = 2; await context.RemoteAgents.AnyAsync(a => a.Name == name); i++)
                name = $"{baseName}-{i}";
            agent = new RemoteAgent { Name = name, MachineId = body.MachineId };
            context.RemoteAgents.Add(agent);
        }
        else
        {
            connections.Log(agent.Id, "info", "Re-enrolled (reinstalled); issued a new secret.");
        }

        agent.SecretHash = AgentSecrets.Hash(secret);
        agent.Hostname = body.Hostname ?? agent.Hostname;
        agent.Rid = body.Rid ?? agent.Rid;
        await context.SaveChangesAsync();
        await connections.DisconnectAsync(agent.Id, "re-enrolled");

        return Results.Json(new AgentEnrollResponse { AgentId = agent.Id, Secret = secret, Name = agent.Name }, AgentProtocol.JsonOptions);
    }

    private static async Task ConnectAsync(HttpContext http, LRDbContext context, AgentConnectionManager connections)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!int.TryParse(http.Request.Headers[AgentProtocol.ProtocolHeader], out var protocol) || protocol != AgentProtocol.Version)
        {
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        var agent = await AuthenticateAgentAsync(http.Request, context);
        if (agent is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!agent.IsEnabled)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var remote = http.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true }) remote = remote.MapToIPv4();
        var remoteAddress = remote is null ? "unknown"
            : IPAddress.IsLoopback(remote) ? "127.0.0.1"
            : remote.ToString();

        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        await connections.RunAsync(agent.Id, socket, remoteAddress, http.RequestAborted);
    }

    /// <summary>The agent named by the id header whose secret matches the bearer token, or null.</summary>
    private static async Task<RemoteAgent?> AuthenticateAgentAsync(HttpRequest request, LRDbContext context)
    {
        if (!Guid.TryParse(request.Headers[AgentProtocol.AgentIdHeader], out var agentId)) return null;
        var token = BearerToken(request);
        if (string.IsNullOrEmpty(token)) return null;
        var agent = await context.RemoteAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId);
        return agent is not null && AgentSecrets.Matches(AgentSecrets.Hash(token), agent.SecretHash) ? agent : null;
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }
}
