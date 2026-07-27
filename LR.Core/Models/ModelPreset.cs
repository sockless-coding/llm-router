namespace LR.Core.Models;

/// <summary>
/// A named preset that defines how a server instance should load a model.
/// </summary>
public class ModelPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The server instance this preset belongs to.
    /// </summary>
    public Guid ServerInstanceId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Context length (e.g., 4096, 8192).
    /// </summary>
    public int ContextLength { get; set; }

    /// <summary>
    /// Number of layers to offload to GPU (-1 = all).
    /// </summary>
    public int GpuLayers { get; set; } = -1;

    /// <summary>
    /// Free-form backend flags (e.g., "--mlock", "--gpu-split").
    /// </summary>
    public Dictionary<string, string> Flags { get; set; } = new();
}
