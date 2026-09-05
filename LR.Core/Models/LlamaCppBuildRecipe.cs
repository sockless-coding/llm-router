using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A reusable recipe for compiling llama.cpp from source: which repo/ref to build, the CMake
/// configuration, and how to collect the resulting binaries. Recipes are saved once and re-run
/// (e.g. to rebuild against a newer commit). Built-in templates are seeded on startup and marked
/// <see cref="IsBuiltIn"/>.
/// </summary>
[Table("LlamaCppBuildRecipes")]
public class LlamaCppBuildRecipe
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1024)]
    public string? Description { get; set; }

    /// <summary>The GPU backend the produced build targets (also drives update checks and routing).</summary>
    public BackendType BackendType { get; set; }

    [MaxLength(512)]
    public string GitRepoUrl { get; set; } = "https://github.com/ggml-org/llama.cpp";

    /// <summary>Branch, tag, or commit SHA to build. A source build may override this per-run.</summary>
    [MaxLength(256)]
    public string GitRef { get; set; } = "master";

    /// <summary>
    /// CMake configure arguments, one token per entry
    /// (e.g. <c>-DGGML_SYCL=ON</c>, <c>-DGGML_SYCL_F16=ON</c>). Stored as JSON.
    /// </summary>
    [Column(TypeName = "TEXT")]
    public List<string> CMakeArgs { get; set; } = new();

    /// <summary>CMake generator (e.g. <c>Ninja</c>). Null lets CMake pick its platform default.</summary>
    [MaxLength(128)]
    public string? CMakeGenerator { get; set; }

    [MaxLength(64)]
    public string BuildConfig { get; set; } = "Release";

    /// <summary>
    /// Shell command sourced before running git/cmake so toolchain vars are set — e.g.
    /// <c>"C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64</c> for SYCL on Windows, or a
    /// <c>setvars.sh</c> on Linux. Mirrors <see cref="BackendConfig.EnvironmentSetupCommand"/>.
    /// </summary>
    [MaxLength(2048)]
    public string? EnvironmentSetupCommand { get; set; }

    /// <summary>
    /// Extra glob patterns (relative to the build directory) to copy into the output folder on top
    /// of the default <c>build/bin/*</c>. Stored as JSON.
    /// </summary>
    [Column(TypeName = "TEXT")]
    public List<string> ExtraArtifactGlobs { get; set; } = new();

    /// <summary>True for seeded templates. Built-in recipes can be duplicated but not deleted.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
