using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;

using LR.Core.Agents;

namespace LR.Agent;

/// <summary>
/// Replaces this agent's files with the package the router serves and restarts. Files are swapped
/// in place: on Linux a rename over the old file (running processes keep the old inode); on Windows
/// the running files are first renamed aside (allowed while they're in use) and cleaned up on the
/// next start. Wrappers run as separate processes, so backends keep serving across the restart.
/// </summary>
public sealed class SelfUpdater
{
    private const string OldSuffix = ".lr-old";

    /// <summary>Exit code that tells the service manager (systemd Restart=always, SCM recovery) to start us again.</summary>
    public const int RestartExitCode = 75;

    private readonly AgentConfig _config;
    private readonly AgentSession _session;
    private readonly ILogger<SelfUpdater> _logger;
    private int _updating;

    public SelfUpdater(AgentConfig config, AgentSession session, ILogger<SelfUpdater> logger)
    {
        _config = config;
        _session = session;
        _logger = logger;
    }

    public async Task UpdateAsync(UpdateAgentMessage update, CancellationToken ct)
    {
        if (string.Equals(update.Version, AgentInfo.Version, StringComparison.OrdinalIgnoreCase))
            return;
        if (Interlocked.Exchange(ref _updating, 1) == 1)
            return;

        var updateRoot = Path.Combine(_config.ResolvedDataDirectory, ".update");
        try
        {
            await ReportAsync("info", $"Updating from {AgentInfo.Version} to {update.Version}…");
            if (Directory.Exists(updateRoot)) Directory.Delete(updateRoot, recursive: true);
            Directory.CreateDirectory(updateRoot);

            var rid = AgentProtocol.CurrentRid();
            var archive = Path.Combine(updateRoot, AgentProtocol.PackageFileName(rid));
            await DownloadAsync(rid, archive, ct);

            await using (var stream = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
                if (!string.IsNullOrEmpty(update.Sha256) && !actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Checksum mismatch for the downloaded package (expected {update.Sha256}, got {actual}).");
            }

            var extracted = Path.Combine(updateRoot, "files");
            Directory.CreateDirectory(extracted);
            if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);
            else
            {
                await using var fs = File.OpenRead(archive);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, extracted, overwriteFiles: true, ct);
            }

            var exeName = Path.GetFileName(AgentInfo.ExecutablePath);
            if (!File.Exists(Path.Combine(extracted, exeName)))
                throw new InvalidOperationException($"The package doesn't contain {exeName}.");

            ApplyFiles(extracted, AppContext.BaseDirectory);
            await ReportAsync("info", $"Updated to {update.Version}; restarting.");
            _logger.LogInformation("Agent files replaced with {Version}; restarting.", update.Version);
            try { Directory.Delete(updateRoot, recursive: true); } catch { /* best effort */ }
            Restart();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent update failed.");
            await ReportAsync("error", $"Update to {update.Version} failed: {ex.Message}");
            Interlocked.Exchange(ref _updating, 0);
        }
    }

    /// <summary>Exits for the service manager to restart us, or — when run by hand — relaunches ourselves first.</summary>
    public void Restart()
    {
        bool managed = (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()) || SystemdHelpers.IsSystemdService();
        if (!managed)
        {
            var startInfo = new ProcessStartInfo { FileName = AgentInfo.ExecutablePath, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                startInfo.ArgumentList.Add(arg);
            Process.Start(startInfo)?.Dispose();
            Environment.Exit(0);
        }
        Environment.Exit(RestartExitCode);
    }

    /// <summary>Removes files renamed aside by a previous update (on Windows they couldn't be deleted while running).</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*" + OldSuffix + "*", SearchOption.AllDirectories))
                try { File.Delete(file); } catch { /* still in use by a wrapper started from the old version */ }
        }
        catch { /* best effort */ }
    }

    private async Task DownloadAsync(string rid, string destination, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Secret);
        http.DefaultRequestHeaders.Add(AgentProtocol.AgentIdHeader, _config.AgentId.ToString());
        using var response = await http.GetAsync(new Uri(new Uri(_config.RouterUrl), $"{AgentProtocol.PackagePath}/{rid}"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, ct);
    }

    private static void ApplyFiles(string sourceRoot, string targetRoot)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            if (relative.Equals("agent.json", StringComparison.OrdinalIgnoreCase))
                continue; // never clobber this install's identity

            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            if (OperatingSystem.IsWindows() && File.Exists(target))
            {
                // A running .exe/.dll can be renamed but not overwritten.
                var aside = target + OldSuffix + DateTime.UtcNow.Ticks;
                File.Move(target, aside);
            }
            File.Move(source, target, overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Archives built on Windows carry no exec bits — restore them on the entry points.
            foreach (var name in new[] { "LR.Agent", "createdump" })
            {
                var path = Path.Combine(targetRoot, name);
                if (File.Exists(path))
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path)
                        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                        | UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }
    }

    private Task ReportAsync(string level, string message) =>
        _session.SendAsync(new AgentLogMessage { Level = level, Message = message });
}
