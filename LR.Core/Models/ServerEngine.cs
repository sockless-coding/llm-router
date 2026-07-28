namespace LR.Core.Models;

/// <summary>
/// The server engine type used by a server instance.
/// This determines which inference server software is run (e.g., llama.cpp, Ollama).
/// GPU backend selection (CUDA/Vulkan/SYCL) is determined by the build folder configured per-server.
/// </summary>
public enum ServerEngine
{
    LlamaCpp = 0,
    Ollama = 1,
}
