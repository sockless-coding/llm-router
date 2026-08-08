namespace LR.Core.Interfaces;

/// <summary>
/// Optional capability implemented by providers backed by a standalone wrapper process,
/// surfaced purely for diagnostics UI (e.g. the server Detail page). Not part of
/// <see cref="IBackendProvider"/> itself since not every engine provider needs it.
/// </summary>
public interface IWrapperDiagnostics
{
    /// <summary>Process ID of the wrapper process, if one is currently connected.</summary>
    int? WrapperPid { get; }

    /// <summary>Process ID of the managed server process, if one is currently running.</summary>
    int? ServerPid { get; }
}
