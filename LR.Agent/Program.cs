using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using LR.Agent;
using LR.Core.Wrapper;

// `LR.Agent wrapper --instance-id … --state-dir …` — this same binary doubles as the per-server
// supervisor, so an agent install is one self-contained folder and an agent update/restart never
// takes running backends down with it.
if (args.Length > 0 && args[0] == "wrapper")
{
    if (!WrapperServer.TryParseArgs(args.Skip(1).ToList(), out var instanceId, out var stateDir))
    {
        Console.Error.WriteLine("Usage: LR.Agent wrapper --instance-id <guid> --state-dir <path>");
        return 1;
    }
    return await WrapperServer.RunAsync(instanceId, stateDir);
}

if (args.Contains("--version"))
{
    Console.WriteLine(AgentInfo.Version);
    return 0;
}

string configPath = AgentConfig.DefaultPath;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--config") configPath = Path.GetFullPath(args[i + 1]);

var config = AgentConfig.Load(configPath);
for (int i = 0; i < args.Length - 1; i++)
{
    // Allow `LR.Agent --router-url … --enrollment-token …` for a quick manual run.
    if (args[i] == "--router-url") config.RouterUrl = args[i + 1].TrimEnd('/');
    else if (args[i] == "--enrollment-token") config.EnrollmentToken = args[i + 1];
    else if (args[i] == "--data-dir") config.DataDirectory = args[i + 1];
}

// `LR.Agent configure --router-url … --enrollment-token … [--data-dir …]` — used by the install
// scripts: writes agent.json, keeping an existing install's identity (a reinstall re-enrolls only
// if the router no longer knows it).
if (args.Length > 0 && args[0] == "configure")
{
    config.Save();
    Console.WriteLine($"Wrote {config.FilePath} (router {config.RouterUrl}, data in {config.ResolvedDataDirectory}).");
    return 0;
}

if (string.IsNullOrWhiteSpace(config.RouterUrl))
{
    Console.Error.WriteLine($"No router URL configured. Set \"routerUrl\" in {configPath} or pass --router-url.");
    return 1;
}

Directory.CreateDirectory(config.ResolvedDataDirectory);
SelfUpdater.CleanupLeftovers();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AgentInfo.WindowsServiceName);
builder.Services.AddSystemd();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddSingleton<WrapperRelay>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<SelfUpdater>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
return Environment.ExitCode;

namespace LR.Agent
{
    public static class AgentInfo
    {
        public const string WindowsServiceName = "LRAgent";
        public const string SystemdUnitName = "lr-agent";

        public static string Version { get; } =
            typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } v
                ? v.Split('+')[0]
                : typeof(AgentInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        /// <summary>Path of this executable — what wrappers are launched from and what updates replace.</summary>
        public static string ExecutablePath => Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "LR.Agent.exe" : "LR.Agent");
    }
}
