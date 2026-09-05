using System.Diagnostics;

using LR.Core.Wrapper;

namespace LR.Core.Services;

/// <summary>
/// Cross-platform command runner for build tooling (git, cmake). Streams merged stdout+stderr
/// line-by-line and kills the whole process tree on cancellation.
///
/// When <c>environmentSetupCommand</c> is set it runs the command through a throwaway shell script
/// that sources that command first, so toolchain vars (e.g. Intel oneAPI <c>setvars</c>) are in
/// scope — the same trick <see cref="LR.Wrapper"/>'s host uses to launch <c>llama-server</c> under
/// a SYCL environment, generalised to bash on non-Windows hosts.
/// </summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, bool Cancelled);

    public static async Task<Result> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? environmentSetupCommand,
        Func<string, Task> onOutputLine,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        string? tempScript = null;
        if (string.IsNullOrWhiteSpace(environmentSetupCommand))
        {
            startInfo.FileName = executable;
            foreach (var a in arguments) startInfo.ArgumentList.Add(a);
        }
        else if (OperatingSystem.IsWindows())
        {
            tempScript = await WriteWindowsScriptAsync(executable, arguments, environmentSetupCommand!);
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c \"" + tempScript + "\"";
        }
        else
        {
            tempScript = await WritePosixScriptAsync(executable, arguments, environmentSetupCommand!);
            startInfo.FileName = "/bin/bash";
            startInfo.ArgumentList.Add(tempScript);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var outputChannel = System.Threading.Channels.Channel.CreateUnbounded<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) outputChannel.Writer.TryWrite(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) outputChannel.Writer.TryWrite(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var pumpTask = Task.Run(async () =>
        {
            await foreach (var line in outputChannel.Reader.ReadAllAsync())
            {
                try { await onOutputLine(line); } catch { /* logging must not kill the build */ }
            }
        });

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* best effort */ }
        }
        finally
        {
            outputChannel.Writer.TryComplete();
            await pumpTask;
            TryDelete(tempScript);
        }

        return new Result(cancelled ? -1 : process.ExitCode, cancelled);
    }

    private static async Task<string> WriteWindowsScriptAsync(string exe, IReadOnlyList<string> args, string envSetup)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lr-build-{Guid.NewGuid():N}.bat");
        var lines = new[]
        {
            "@echo off",
            $"call {envSetup}",
            "if errorlevel 1 exit /b 1",
            $"call \"{exe}\" {WindowsCommandLine.Join(args)}",
        };
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    private static async Task<string> WritePosixScriptAsync(string exe, IReadOnlyList<string> args, string envSetup)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lr-build-{Guid.NewGuid():N}.sh");
        var quoted = string.Join(' ', args.Select(PosixQuote));
        var lines = new[]
        {
            "#!/bin/bash",
            "set -e",
            envSetup,
            $"exec {PosixQuote(exe)} {quoted}",
        };
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    private static string PosixQuote(string s) =>
        s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ':')
            ? s
            : "'" + s.Replace("'", "'\\''") + "'";

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
