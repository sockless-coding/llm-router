using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

using LR.Core.Wrapper;
using LR.Wrapper;

Guid instanceId = Guid.Empty;
string? stateDir = null;

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--instance-id")
        instanceId = Guid.Parse(args[i + 1]);
    else if (args[i] == "--state-dir")
        stateDir = args[i + 1];
}

if (instanceId == Guid.Empty || string.IsNullOrEmpty(stateDir))
{
    Console.Error.WriteLine("Usage: LR.Wrapper --instance-id <guid> --state-dir <path>");
    return 1;
}

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
