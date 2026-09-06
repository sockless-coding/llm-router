using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// The recipe templates seeded on startup (<see cref="LlamaCppBuildRecipe.IsBuiltIn"/>). They're a
/// starting point — users duplicate one and adjust for their machine.
///
/// The CMake flags / generator / environment-setup commands here are taken from llama.cpp's own
/// build documentation on the <c>master</c> branch, not invented:
/// <list type="bullet">
///   <item><c>docs/build.md</c> — per-backend <c>cmake</c> invocations (CPU, CUDA, Vulkan, HIP,
///     Metal, MUSA)</item>
///   <item><c>docs/backend/SYCL.md</c> + <c>examples/sycl/*</c> — SYCL FP32 / FP16</item>
///   <item><c>docs/backend/OPENCL.md</c>, <c>docs/backend/OPENVINO.md</c>, <c>docs/backend/CANN.md</c></item>
/// </list>
/// <c>-DLLAMA_CURL=OFF</c> is our own addition (default is ON and needs libcurl dev headers) — the
/// router doesn't use <c>llama-server</c>'s built-in model downloader. <c>CMAKE_BUILD_TYPE</c> is
/// supplied by the pipeline from <see cref="LlamaCppBuildRecipe.BuildConfig"/>, so it's not repeated
/// in the arg lists here. Cross-platform backends carry the values for the host llm-router runs on
/// (Windows values on Windows, POSIX values elsewhere); the "Load reference" panel in the editor
/// shows every documented variant.
/// </summary>
public static class BuiltInRecipeTemplates
{
    private static bool Win => OperatingSystem.IsWindows();

    // docs/backend/SYCL.md: Windows uses `"...\setvars.bat" intel64 --force`; Linux sources setvars.sh.
    private static string SyclEnvSetup => Win
        ? "\"C:\\Program Files (x86)\\Intel\\oneAPI\\setvars.bat\" intel64 --force"
        : "source /opt/intel/oneapi/setvars.sh";

    // docs/backend/SYCL.md: Windows -> C compiler `cl` (MSVC), C++ `icx`; Linux -> `icx` / `icpx`.
    private static IEnumerable<string> SyclCompilerArgs => Win
        ? new[] { "-DCMAKE_C_COMPILER=cl", "-DCMAKE_CXX_COMPILER=icx" }
        : new[] { "-DCMAKE_C_COMPILER=icx", "-DCMAKE_CXX_COMPILER=icpx" };

    public static IEnumerable<LlamaCppBuildRecipe> All()
    {
        yield return new LlamaCppBuildRecipe
        {
            Name = "CPU (portable)",
            Description = "Portable CPU build (GGML_NATIVE=OFF, so no -march=native) — runs on any x64 host. Source: docs/build.md.",
            BackendType = BackendType.Cpu,
            CMakeArgs = new() { "-DGGML_NATIVE=OFF", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "CUDA",
            Description = "NVIDIA CUDA build. Needs the CUDA Toolkit (nvcc). GGML_NATIVE=OFF covers all GPU archs. Source: docs/build.md.",
            BackendType = BackendType.Cuda,
            CMakeArgs = new() { "-DGGML_CUDA=ON", "-DGGML_NATIVE=OFF", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "Vulkan",
            Description = "Cross-vendor Vulkan build. Needs the Vulkan SDK (glslc). Source: docs/build.md.",
            BackendType = BackendType.Vulkan,
            CMakeArgs = new() { "-DGGML_VULKAN=ON", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "SYCL FP32",
            Description = "Intel oneAPI SYCL build (FP32), matching the official SYCL release. Source: docs/backend/SYCL.md.",
            BackendType = BackendType.Sycl,
            CMakeGenerator = Win ? "Ninja" : null,
            EnvironmentSetupCommand = SyclEnvSetup,
            CMakeArgs = new List<string> { "-DGGML_SYCL=ON" }.Concat(SyclCompilerArgs).Append("-DLLAMA_CURL=OFF").ToList(),
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "SYCL FP16",
            Description = "Intel oneAPI SYCL build with GGML_SYCL_F16=ON — the half-precision path the official Windows SYCL binaries don't ship. Source: docs/backend/SYCL.md.",
            BackendType = BackendType.Sycl,
            CMakeGenerator = Win ? "Ninja" : null,
            EnvironmentSetupCommand = SyclEnvSetup,
            CMakeArgs = new List<string> { "-DGGML_SYCL=ON", "-DGGML_SYCL_F16=ON" }.Concat(SyclCompilerArgs).Append("-DLLAMA_CURL=OFF").ToList(),
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "HIP / ROCm",
            Description = "AMD ROCm/HIP build. Needs ROCm installed. Set GPU_TARGETS to your GPU arch " +
                          "(gfx1100 = RX 7900, gfx1030 = RX 6800/6900; check with rocminfo). Source: docs/build.md.",
            BackendType = BackendType.Hip,
            CMakeGenerator = Win ? "Ninja" : null,
            EnvironmentSetupCommand = Win
                ? "set PATH=%HIP_PATH%\\bin;%PATH%"
                : "export HIPCXX=\"$(hipconfig -l)/clang\"; export HIP_PATH=\"$(hipconfig -R)\"",
            CMakeArgs = Win
                ? new() { "-DGGML_HIP=ON", "-DGPU_TARGETS=gfx1100", "-DCMAKE_C_COMPILER=clang", "-DCMAKE_CXX_COMPILER=clang++", "-DLLAMA_CURL=OFF" }
                : new() { "-DGGML_HIP=ON", "-DGPU_TARGETS=gfx1030", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "Metal (macOS)",
            Description = "Apple Metal build — macOS only. Metal is on by default; METAL_EMBED_LIBRARY bundles " +
                          "the shader so the output folder is relocatable. Source: docs/build.md.",
            BackendType = BackendType.Metal,
            CMakeArgs = new() { "-DGGML_METAL=ON", "-DGGML_METAL_EMBED_LIBRARY=ON", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "OpenCL (Adreno / WoA)",
            Description = "OpenCL build for Qualcomm Adreno GPUs on Windows-on-ARM (and Linux). Requires the " +
                          "OpenCL headers/loader built first and, on Windows-ARM, an LLVM toolchain file — " +
                          "see docs/backend/OPENCL.md (use the reference panel).",
            BackendType = BackendType.OpenCL,
            CMakeGenerator = "Ninja",
            CMakeArgs = new() { "-DGGML_OPENCL=ON", "-DBUILD_SHARED_LIBS=OFF", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "OpenVINO",
            Description = "Intel OpenVINO build (CPU / GPU / NPU). Needs the OpenVINO toolkit; on Windows also a " +
                          "vcpkg toolchain file (adjust the path). Source: docs/backend/OPENVINO.md.",
            BackendType = BackendType.OpenVino,
            CMakeGenerator = "Ninja",
            EnvironmentSetupCommand = Win ? "C:\\Intel\\openvino\\setupvars.bat" : "source /opt/intel/openvino/setupvars.sh",
            CMakeArgs = Win
                ? new() { "-DGGML_OPENVINO=ON", "-DCMAKE_TOOLCHAIN_FILE=C:\\vcpkg\\scripts\\buildsystems\\vcpkg.cmake", "-DLLAMA_CURL=OFF" }
                : new() { "-DGGML_OPENVINO=ON", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "MUSA",
            Description = "Moore Threads MUSA build (Linux). Needs the MUSA SDK. Optionally add " +
                          "-DMUSA_ARCHITECTURES=\"21\" to target one arch. Source: docs/build.md.",
            BackendType = BackendType.Musa,
            CMakeArgs = new() { "-DGGML_MUSA=ON", "-DLLAMA_CURL=OFF" },
        };

        yield return new LlamaCppBuildRecipe
        {
            Name = "CANN",
            Description = "Huawei Ascend CANN build (Linux). Needs the CANN toolkit; SoC is auto-detected. Source: docs/backend/CANN.md.",
            BackendType = BackendType.Cann,
            EnvironmentSetupCommand = "source /usr/local/Ascend/cann/set_env.sh",
            CMakeArgs = new() { "-DGGML_CANN=on", "-DLLAMA_CURL=OFF" },
        };
    }
}
