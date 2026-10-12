using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

using LR.Core.Services.AppUpdates;

namespace LR.Application.Services;

/// <summary>
/// The second half of a dashboard-triggered update: <see cref="AppUpdateService"/> stages the new
/// release and launches its <c>LR.Application.exe</c> with <see cref="Flag"/>. Running from the
/// staging folder, outside the service, this process stops the service, swaps the files (rolling
/// back on failure), records the result and starts the service again. The argument names are a
/// contract between the running version and the next one — keep them stable.
/// </summary>
public static class UpdateApplyCommand
{
    public const string Flag = "--apply-update";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(1);
    private const int ApplyAttempts = 3;

    [SupportedOSPlatform("windows")]
    public static int Run(string[] args)
    {
        var options = ParseArgs(args);
        string Require(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{key}");

        var source = Require("source");
        var target = Require("target");
        var backup = Require("backup");
        var serviceName = Require("service");
        var logPath = Require("log");
        options.TryGetValue("from", out var fromVersion);
        options.TryGetValue("to", out var toVersion);
        var routerPid = int.TryParse(options.GetValueOrDefault("pid"), out var pid) ? pid : (int?)null;

        void Log(string message)
        {
            try { File.AppendAllText(logPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}"); }
            catch { /* logging must never fail the update */ }
        }

        var result = new AppUpdateResult { FromVersion = fromVersion, ToVersion = toVersion };
        Log($"Updating {fromVersion} -> {toVersion}: {source} -> {target} (service '{serviceName}')");

        using var service = new ServiceController(serviceName);
        try
        {
            // Give the dashboard's "install" request time to get its response out before the
            // router goes away.
            Thread.Sleep(TimeSpan.FromSeconds(2));
            StopService(service, routerPid, Log);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    UpdateInstaller.Apply(source, target, backup, Log);
                    break;
                }
                catch (Exception ex) when (attempt < ApplyAttempts)
                {
                    // Antivirus scanners or a slow-exiting handle can briefly hold a file; Apply
                    // rolled back, so trying again starts from a clean slate.
                    Log($"Attempt {attempt} failed ({ex.Message}); retrying.");
                    Thread.Sleep(TimeSpan.FromSeconds(3));
                }
            }

            result.Success = true;
            result.Message = $"Updated to {toVersion}.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Update to {toVersion} failed: {ex.Message}";
            Log(result.Message);
        }

        result.CompletedAtUtc = DateTime.UtcNow;
        try { result.Write(UpdateInstaller.GetResultPath(target)); }
        catch (Exception ex) { Log($"Could not write result file: {ex.Message}"); }

        // Start the service either way — on failure the old version was restored.
        try
        {
            service.Refresh();
            if (service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
            }
            Log("Service started.");
        }
        catch (Exception ex)
        {
            Log($"Could not start service '{serviceName}': {ex.Message}");
            return 2;
        }

        return result.Success ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static void StopService(ServiceController service, int? routerPid, Action<string> log)
    {
        service.Refresh();
        if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
        {
            log("Stopping service.");
            service.Stop();
        }
        service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);

        // SCM reports Stopped slightly before the process has released its files.
        if (routerPid is { } pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.WaitForExit(StopTimeout))
                    throw new System.TimeoutException($"Router process {pid} did not exit.");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        log("Service stopped.");
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--") && args[i] != Flag && i + 1 < args.Length)
                options[args[i][2..]] = args[++i];
        }
        return options;
    }
}
