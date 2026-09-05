using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// The recipe templates seeded on startup (<see cref="LlamaCppBuildRecipe.IsBuiltIn"/>). They're a
/// starting point — users duplicate one and adjust CMake args / the environment-setup command for
/// their machine. The SYCL FP16 template exists specifically because the official SYCL binaries are
/// FP32 only.
/// </summary>
public static class BuiltInRecipeTemplates
{
    private static string? WindowsOneApiSetvars =>
        OperatingSystem.IsWindows()
            ? "\"C:\\Program Files (x86)\\Intel\\oneAPI\\setvars.bat\" intel64"
            : ". /opt/intel/oneapi/setvars.sh";

    public static IEnumerable<LlamaCppBuildRecipe> All()
    {
        yield return new LlamaCppBuildRecipe
        {
            Name = "CPU (portable)",
            Description = "Portable CPU build (no -march=native), works on any x64 host.",
            BackendType = BackendType.Cpu,
            CMakeArgs = new() { "-DGGML_NATIVE=OFF", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "CUDA",
            Description = "NVIDIA CUDA build. Requires the CUDA Toolkit (nvcc) on PATH.",
            BackendType = BackendType.Cuda,
            CMakeArgs = new() { "-DGGML_CUDA=ON", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "Vulkan",
            Description = "Cross-vendor Vulkan build. Requires the Vulkan SDK (glslc) on PATH.",
            BackendType = BackendType.Vulkan,
            CMakeArgs = new() { "-DGGML_VULKAN=ON", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "SYCL FP32",
            Description = "Intel oneAPI SYCL build (FP32), equivalent to the official SYCL release.",
            BackendType = BackendType.Sycl,
            CMakeGenerator = "Ninja",
            EnvironmentSetupCommand = WindowsOneApiSetvars,
            CMakeArgs = new()
            {
                "-DGGML_SYCL=ON",
                "-DCMAKE_C_COMPILER=icx",
                OperatingSystem.IsWindows() ? "-DCMAKE_CXX_COMPILER=icx" : "-DCMAKE_CXX_COMPILER=icpx",
                "-DLLAMA_CURL=OFF",
            },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "SYCL FP16",
            Description = "Intel oneAPI SYCL build with GGML_SYCL_F16=ON — half-precision path the " +
                          "official SYCL binaries don't ship.",
            BackendType = BackendType.Sycl,
            CMakeGenerator = "Ninja",
            EnvironmentSetupCommand = WindowsOneApiSetvars,
            CMakeArgs = new()
            {
                "-DGGML_SYCL=ON",
                "-DGGML_SYCL_F16=ON",
                "-DCMAKE_C_COMPILER=icx",
                OperatingSystem.IsWindows() ? "-DCMAKE_CXX_COMPILER=icx" : "-DCMAKE_CXX_COMPILER=icpx",
                "-DLLAMA_CURL=OFF",
            },
        };
    }
}
