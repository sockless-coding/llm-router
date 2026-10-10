using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// The set of server engines this router can run, as registered <see cref="IEngineDescriptor"/>s.
/// Engines present in <see cref="ServerEngine"/> but without a descriptor (no provider yet) are
/// not listed, so the UI never offers an engine that can't be started.
/// </summary>
public interface IEngineCatalog
{
    /// <summary>All registered engines, in registration order.</summary>
    IReadOnlyList<IEngineDescriptor> All { get; }

    /// <summary>The descriptor for <paramref name="engine"/>, or null if it isn't registered.</summary>
    IEngineDescriptor? Get(ServerEngine engine);
}
