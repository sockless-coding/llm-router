namespace LR.Core.Models;

/// <summary>
/// The server engine type used by a server instance.
/// This determines which inference server software is run (e.g., llama.cpp, Strata).
/// GPU backend selection (CUDA/Vulkan/SYCL) is determined by the build folder configured per-server.
/// Values are persisted — append new engines, never renumber. An engine is only offered in the UI
/// once it has an <see cref="LR.Core.Interfaces.IEngineDescriptor"/> registered.
/// </summary>
public enum ServerEngine
{
    LlamaCpp = 0,

    /// <summary>Reserved; no provider is implemented yet.</summary>
    Ollama = 1,

    /// <summary>Strata (github.com/Niko1221/Strata) — MoE-offloading engine with a Python HTTP frontend.</summary>
    Strata = 2,
}
