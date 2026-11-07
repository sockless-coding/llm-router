using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace LR.Core.Wrapper;

/// <summary>
/// The wrapper process's main loop: writes its state file, then serves one named-pipe connection
/// at a time (router or agent), handing each message to a <see cref="WrapperHost"/> that outlives
/// every connection. Shared by the standalone <c>LR.Wrapper</c> executable (used by the router for
/// local servers) and <c>LR.Agent wrapper …</c> (used by a remote agent), so both run the exact
/// same supervisor.
/// </summary>
public static class WrapperServer
{
    /// <summary>Parses <c>--instance-id &lt;guid&gt; --state-dir &lt;path&gt;</c>; returns false when either is missing.</summary>
    public static bool TryParseArgs(IReadOnlyList<string> args, out Guid instanceId, out string stateDir)
    {
        instanceId = Guid.Empty;
        stateDir = string.Empty;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--instance-id" && Guid.TryParse(args[i + 1], out var id))
                instanceId = id;
            else if (args[i] == "--state-dir")
                stateDir = args[i + 1];
        }
        return instanceId != Guid.Empty && !string.IsNullOrEmpty(stateDir);
    }

    public static async Task<int> RunAsync(Guid instanceId, string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        string pipeName = WrapperConventions.GetPipeName(instanceId);
        string stateFilePath = WrapperConventions.GetStateFilePath(stateDir, instanceId);

        var currentProcess = Process.GetCurrentProcess();
        var state = new WrapperStateFile
        {
            InstanceId = instanceId,
            WrapperPid = currentProcess.Id,
            PipeName = pipeName,
            WrapperStartedAtUtc = currentProcess.StartTime.ToUniversalTime(),
        };

        // Atomic write (temp file + move) so a concurrently-running reconciliation scan never observes
        // a partially-written state file.
        string tempStatePath = stateFilePath + ".tmp";
        await File.WriteAllTextAsync(tempStatePath, JsonSerializer.Serialize(state));
        File.Move(tempStatePath, stateFilePath, overwrite: true);

        var host = new WrapperHost();
        using var shutdownCts = new CancellationTokenSource();

        // Clean up on Ctrl+C/SIGTERM so the state file doesn't linger as a false orphan for the
        // next router boot to trip over.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdownCts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownCts.Cancel();

        try
        {
            while (!shutdownCts.IsCancellationRequested)
            {
                using var pipeServer = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                try
                {
                    await pipeServer.WaitForConnectionAsync(shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var connection = new WrapperPipeConnection(pipeServer);
                host.SetConnection(connection);
                try
                {
                    await connection.SendAsync(host.BuildHello());

                    while (true)
                    {
                        var message = await connection.ReceiveAsync(shutdownCts.Token);
                        if (message is null) break; // client disconnected

                        await host.HandleMessageAsync(connection, message);

                        if (host.ShutdownRequested)
                            break;
                    }
                }
                catch (IOException)
                {
                    // Client disconnected abruptly (e.g. router process died) — loop back and wait
                    // for the next connection. The managed processes are untouched.
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    host.ClearConnection();
                    await connection.DisposeAsync();
                }

                if (host.ShutdownRequested)
                    break;
            }
        }
        finally
        {
            await host.StopEverythingAsync();
            try { File.Delete(stateFilePath); } catch { /* best effort */ }
        }

        return 0;
    }

    /// <summary>
    /// Whether the wrapper described by <paramref name="state"/> is still the process holding its
    /// PID (guards against PID reuse by comparing start times).
    /// </summary>
    public static bool IsAlive(WrapperStateFile state)
    {
        try
        {
            using var proc = Process.GetProcessById(state.WrapperPid);
            if (proc.HasExited) return false;
            return Math.Abs((proc.StartTime.ToUniversalTime() - state.WrapperStartedAtUtc).TotalSeconds) <= 5;
        }
        catch
        {
            return false;
        }
    }
}
