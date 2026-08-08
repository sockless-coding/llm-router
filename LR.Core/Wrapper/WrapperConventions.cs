namespace LR.Core.Wrapper;

/// <summary>
/// Naming/path conventions shared by the router (LR.Providers/LR.Application) and LR.Wrapper,
/// so both sides derive the same pipe name and state file location without extra round trips.
/// </summary>
public static class WrapperConventions
{
    /// <summary>
    /// Deterministic pipe name for a given server instance — both the wrapper and the router
    /// compute this independently, so no discovery round trip is needed to connect.
    /// </summary>
    public static string GetPipeName(Guid instanceId) => $"lr-wrapper-{instanceId:N}";

    /// <summary>
    /// Default directory wrapper state files live in, sibling to the app's own "data/lr.db"
    /// convention. Shared by LR.Providers (writing/reading per-instance state) and
    /// LR.Application (scanning for orphaned wrappers on boot).
    /// </summary>
    public static string GetDefaultStateDirectory(string appBaseDirectory) =>
        Path.Combine(appBaseDirectory, "data", "wrappers");

    /// <summary>
    /// Path to the per-instance state file a wrapper writes on boot and deletes on clean exit.
    /// </summary>
    public static string GetStateFilePath(string stateDirectory, Guid instanceId) =>
        Path.Combine(stateDirectory, $"{instanceId:N}.json");

    /// <summary>
    /// Name of the wrapper executable, matching the OS-suffix convention already used for the
    /// llama-server executable itself.
    /// </summary>
    public static string GetWrapperExecutableName() =>
        OperatingSystem.IsWindows() ? "LR.Wrapper.exe" : "LR.Wrapper";
}

/// <summary>
/// Contents of a wrapper's on-disk state file, used by the router's boot-time reconciliation
/// pass to discover and re-attach to wrappers that outlived a previous router process.
/// </summary>
public sealed class WrapperStateFile
{
    public Guid InstanceId { get; set; }
    public int WrapperPid { get; set; }
    public string PipeName { get; set; } = string.Empty;
    public DateTime WrapperStartedAtUtc { get; set; }
}
