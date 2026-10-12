using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

using Microsoft.Extensions.Hosting.WindowsServices;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.AppUpdates;

namespace LR.Application.Services;

public enum AppUpdateState
{
    Idle,
    Checking,
    Downloading,
    /// <summary>The updater has been launched; the service is about to stop and restart.</summary>
    Restarting,
    Failed,
}

/// <summary>
/// Checks the router's own GitHub releases and installs a newer one from the dashboard. Installing
/// downloads the archive matching this install's flavor, verifies its digest, extracts it to a
/// staging folder and launches the staged <c>LR.Application.exe</c> in
/// <see cref="UpdateApplyCommand"/> mode, which restarts the service onto the new files. Only
/// offered when running as a Windows Service, since that's what lets the updater bring the router
/// back up afterwards.
/// </summary>
public sealed class AppUpdateService
{
    private static readonly TimeSpan CheckCacheDuration = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AppUpdateService> _logger;
    private readonly object _lock = new();
    private readonly string _installDirectory = AppContext.BaseDirectory;

    private AppUpdateState _state = AppUpdateState.Idle;
    private string? _message;
    private int? _percent;
    private GitHubRelease? _latestRelease;
    private Version? _latestVersion;
    private DateTime? _lastCheckedUtc;

    public AppUpdateService(IServiceScopeFactory scopeFactory, ILogger<AppUpdateService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        CurrentVersion = AppRelease.TryParseVersion(informational, out var version) ? version : new Version(0, 0, 0, 0);
        IsSelfContained = File.Exists(Path.Combine(_installDirectory, "coreclr.dll"));
    }

    public Version CurrentVersion { get; }

    /// <summary>Self-contained installs ship the .NET runtime (<c>coreclr.dll</c>) next to the exe.</summary>
    public bool IsSelfContained { get; }

    /// <summary>Why installing from the dashboard isn't possible here, or null when it is.</summary>
    public string? InstallUnavailableReason =>
        !OperatingSystem.IsWindows() ? "Automatic updates are only supported on Windows."
        : !WindowsServiceHelpers.IsWindowsService() ? "Automatic install is only available when the router runs as a Windows Service. Download the release and replace the files manually."
        : null;

    public AppUpdateStatus GetStatus()
    {
        lock (_lock)
        {
            return new AppUpdateStatus
            {
                CurrentVersion = AppRelease.Format(CurrentVersion),
                LatestVersion = _latestVersion is null ? null : AppRelease.Format(_latestVersion),
                LatestReleaseUrl = _latestRelease?.HtmlUrl,
                LatestPublishedAt = _latestRelease?.PublishedAt,
                UpdateAvailable = _latestVersion is not null && _latestVersion > CurrentVersion,
                LastCheckedUtc = _lastCheckedUtc,
                State = _state.ToString(),
                Message = _message,
                Percent = _percent,
                CanInstall = InstallUnavailableReason is null,
                InstallUnavailableReason = InstallUnavailableReason,
                LastResult = AppUpdateResult.TryRead(UpdateInstaller.GetResultPath(_installDirectory)),
            };
        }
    }

    public async Task<AppUpdateStatus> CheckAsync(bool force, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_state is AppUpdateState.Downloading or AppUpdateState.Restarting or AppUpdateState.Checking)
                return GetStatus();
            if (!force && _lastCheckedUtc is { } last && DateTime.UtcNow - last < CheckCacheDuration)
                return GetStatus();
            SetState(AppUpdateState.Checking, "Checking for updates…");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var github = scope.ServiceProvider.GetRequiredService<IGitHubClient>();
            var releases = await github.ListReleasesAsync(AppRelease.Repository, 20, ct);
            var latest = AppRelease.PickLatest(releases);

            lock (_lock)
            {
                _latestRelease = latest?.Release;
                _latestVersion = latest?.Version;
                _lastCheckedUtc = DateTime.UtcNow;
                SetState(AppUpdateState.Idle, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            lock (_lock) SetState(AppUpdateState.Failed, $"Update check failed: {ex.Message}");
        }

        return GetStatus();
    }

    /// <summary>
    /// Starts installing the latest release in the background. Throws when an install can't start;
    /// progress and failures after that are reported through <see cref="GetStatus"/>.
    /// </summary>
    public void StartInstall()
    {
        if (InstallUnavailableReason is { } reason)
            throw new InvalidOperationException(reason);

        GitHubRelease release;
        Version version;
        lock (_lock)
        {
            if (_state is AppUpdateState.Downloading or AppUpdateState.Restarting or AppUpdateState.Checking)
                throw new InvalidOperationException("An update is already in progress.");
            if (_latestRelease is null || _latestVersion is null)
                throw new InvalidOperationException("Check for updates first.");
            if (_latestVersion <= CurrentVersion)
                throw new InvalidOperationException($"Already on the latest version ({AppRelease.Format(CurrentVersion)}).");

            release = _latestRelease;
            version = _latestVersion;
            SetState(AppUpdateState.Downloading, "Starting download…", 0);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await InstallAsync(release, version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Installing update {Version} failed", release.TagName);
                lock (_lock) SetState(AppUpdateState.Failed, $"Update failed: {ex.Message}");
            }
        });
    }

    private async Task InstallAsync(GitHubRelease release, Version version)
    {
        var asset = AppRelease.SelectAsset(release, IsSelfContained)
            ?? throw new InvalidOperationException(
                $"Release {release.TagName} has no {(IsSelfContained ? "self-contained" : "framework-dependent")} win-x64 archive.");

        var versionText = AppRelease.Format(version);
        var updatesDirectory = UpdateInstaller.GetUpdatesDirectory(_installDirectory);
        CleanupPreviousUpdates(updatesDirectory);

        var archivePath = Path.Combine(updatesDirectory, "downloads", asset.Name);
        var stagingDirectory = Path.Combine(updatesDirectory, $"staging-{versionText}");
        var backupDirectory = Path.Combine(updatesDirectory, $"backup-{AppRelease.Format(CurrentVersion)}-{DateTime.UtcNow:yyyyMMddHHmmss}");

        _logger.LogInformation("Downloading update {Version} ({Asset})", versionText, asset.Name);
        var progress = new Progress<EngineBuildProgress>(p =>
        {
            lock (_lock) SetState(AppUpdateState.Downloading, p.Message, p.Percent);
        });
        using (var scope = _scopeFactory.CreateScope())
        {
            var github = scope.ServiceProvider.GetRequiredService<IGitHubClient>();
            await github.DownloadAssetAsync(asset.BrowserDownloadUrl, archivePath, progress);
        }

        lock (_lock) SetState(AppUpdateState.Downloading, "Verifying download…", 100);
        await VerifyDigestAsync(archivePath, asset.Digest);

        lock (_lock) SetState(AppUpdateState.Downloading, "Extracting…", 100);
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
        ZipFile.ExtractToDirectory(archivePath, stagingDirectory);
        File.Delete(archivePath);

        var updaterExe = Path.Combine(stagingDirectory, "LR.Application.exe");
        if (!File.Exists(updaterExe))
            throw new InvalidOperationException($"{asset.Name} doesn't contain LR.Application.exe.");

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterExe,
            WorkingDirectory = stagingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            UpdateApplyCommand.Flag,
            "--source", stagingDirectory,
            "--target", _installDirectory.TrimEnd(Path.DirectorySeparatorChar),
            "--backup", backupDirectory,
            "--service", GetServiceName(),
            "--pid", Environment.ProcessId.ToString(),
            "--from", AppRelease.Format(CurrentVersion),
            "--to", versionText,
            "--log", Path.Combine(updatesDirectory, "update.log"),
        })
        {
            startInfo.ArgumentList.Add(arg);
        }

        _logger.LogInformation("Launching updater for {Version}; the service will restart", versionText);
        using (Process.Start(startInfo)) { }
        lock (_lock) SetState(AppUpdateState.Restarting, $"Installing {versionText} — the router is restarting…", 100);
    }

    private static async Task VerifyDigestAsync(string path, string? digest)
    {
        // GitHub only fills in digests for assets uploaded since mid-2025; skip on older ones.
        const string prefix = "sha256:";
        if (string.IsNullOrEmpty(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;

        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        if (!actual.Equals(digest[prefix.Length..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Downloaded archive doesn't match the release's SHA-256 digest.");
    }

    /// <summary>
    /// Removes staging/backup folders from earlier updates. Best effort: a backup can still hold an
    /// <c>LR.Wrapper.exe</c> a running backend has loaded, which can't be deleted until it exits.
    /// </summary>
    private void CleanupPreviousUpdates(string updatesDirectory)
    {
        if (!Directory.Exists(updatesDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(updatesDirectory))
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith("staging-") && !name.StartsWith("backup-") && name != "downloads")
                continue;
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not remove old update folder {Directory}", directory); }
        }
    }

    /// <summary>
    /// The name this process is registered under with the Service Control Manager —
    /// install-service.ps1 lets it differ from the default.
    /// </summary>
    private string GetServiceName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    $"SELECT Name FROM Win32_Service WHERE ProcessId = {Environment.ProcessId}");
                foreach (var service in searcher.Get())
                {
                    if (service["Name"] is string name && name.Length > 0)
                        return name;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not look up the service name; assuming {Default}", DefaultServiceName);
            }
        }
        return DefaultServiceName;
    }

    public const string DefaultServiceName = "LLMRouter";

    private void SetState(AppUpdateState state, string? message, int? percent = null)
    {
        _state = state;
        _message = message;
        _percent = percent;
    }
}

public sealed class AppUpdateStatus
{
    public string CurrentVersion { get; init; } = string.Empty;
    public string? LatestVersion { get; init; }
    public string? LatestReleaseUrl { get; init; }
    public DateTimeOffset? LatestPublishedAt { get; init; }
    public bool UpdateAvailable { get; init; }
    public DateTime? LastCheckedUtc { get; init; }
    public string State { get; init; } = string.Empty;
    public string? Message { get; init; }
    public int? Percent { get; init; }
    public bool CanInstall { get; init; }
    public string? InstallUnavailableReason { get; init; }
    public AppUpdateResult? LastResult { get; init; }
}
