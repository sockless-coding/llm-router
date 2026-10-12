using System.Text.Json;

namespace LR.Core.Services.AppUpdates;

/// <summary>
/// Copies an extracted release over the install folder. Each file being replaced is first moved
/// into a backup folder, which also works on an exe/dll a running process has loaded (e.g.
/// <c>LR.Wrapper.exe</c> under a backend that outlives the router), where overwriting in place
/// would fail. Any error moves every backed-up file back, leaving the old install as it was.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// The install folder's runtime data (database, wrapper state, engine builds, the updater's
    /// own staging/backup folders). Never part of a release and never touched by an update.
    /// </summary>
    public const string DataDirectoryName = "data";

    /// <summary>Where downloads, staging, backups and the last result live, under the install folder.</summary>
    public static string GetUpdatesDirectory(string installDirectory) =>
        Path.Combine(installDirectory, DataDirectoryName, "updates");

    public static string GetResultPath(string installDirectory) =>
        Path.Combine(GetUpdatesDirectory(installDirectory), "last-update.json");

    /// <summary>
    /// Copies every file under <paramref name="sourceDirectory"/> into
    /// <paramref name="targetDirectory"/>, skipping the data folder and any <c>appsettings*.json</c>
    /// the install already has (the user's configuration). Files the release no longer ships are
    /// left in place. Returns the number of files written. <paramref name="backupDirectory"/> must
    /// be on the same volume as the target so moving a locked file is a rename.
    /// </summary>
    public static int Apply(string sourceDirectory, string targetDirectory, string backupDirectory, Action<string>? log = null)
    {
        var replaced = new List<(string Target, string Backup)>();
        var added = new List<string>();

        try
        {
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
                var target = Path.Combine(targetDirectory, relativePath);

                if (IsDataPath(relativePath))
                    continue;
                if (IsUserConfig(relativePath) && File.Exists(target))
                {
                    log?.Invoke($"Keeping existing {relativePath}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(backupDirectory, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup, overwrite: true);
                    replaced.Add((target, backup));
                }
                else
                {
                    added.Add(target);
                }

                File.Copy(sourceFile, target);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Update failed, rolling back: {ex.Message}");
            Rollback(replaced, added, log);
            throw;
        }

        log?.Invoke($"Wrote {replaced.Count + added.Count} files ({replaced.Count} replaced, {added.Count} new).");
        return replaced.Count + added.Count;
    }

    private static void Rollback(List<(string Target, string Backup)> replaced, List<string> added, Action<string>? log)
    {
        foreach (var path in added)
        {
            try { File.Delete(path); }
            catch (Exception ex) { log?.Invoke($"Rollback: could not remove {path}: {ex.Message}"); }
        }

        foreach (var (target, backup) in replaced)
        {
            try
            {
                File.Delete(target);
                File.Move(backup, target);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Rollback: could not restore {target} from {backup}: {ex.Message}");
            }
        }
    }

    private static bool IsDataPath(string relativePath)
    {
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment.Equals(DataDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserConfig(string relativePath) =>
        Path.GetDirectoryName(relativePath) is "" &&
        relativePath.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) &&
        relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Outcome of the last update, written by the updater process (which outlives the router) so the
/// restarted router can report it on the dashboard.
/// </summary>
public sealed class AppUpdateResult
{
    public bool Success { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public string? Message { get; set; }
    public DateTime CompletedAtUtc { get; set; }

    public static AppUpdateResult? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppUpdateResult>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
