using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Default <see cref="IEngineCatalog"/> over the <see cref="IEngineDescriptor"/>s registered in DI.
/// </summary>
public class EngineCatalog : IEngineCatalog
{
    private readonly Dictionary<ServerEngine, IEngineDescriptor> _byEngine;

    public EngineCatalog(IEnumerable<IEngineDescriptor> descriptors)
    {
        All = descriptors.ToList().AsReadOnly();
        _byEngine = All.ToDictionary(d => d.Engine);
    }

    public IReadOnlyList<IEngineDescriptor> All { get; }

    public IEngineDescriptor? Get(ServerEngine engine) =>
        _byEngine.TryGetValue(engine, out var descriptor) ? descriptor : null;
}
