using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Reads metadata from GGUF file headers.
/// </summary>
public interface IGgufMetadataReader
{
    /// <summary>
    /// Reads the GGUF header and returns extracted metadata.
    /// Returns null if the file is not found, invalid, or cannot be read.
    /// </summary>
    Task<GgufMetadata?> ReadAsync(string filePath, CancellationToken ct = default);
}
