using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

public class VllmLayoutTests
{
    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\vllm", "Ubuntu", "/home/me/vllm")]
    [InlineData(@"\\wsl$\Ubuntu-24.04\opt\vllm\", "Ubuntu-24.04", "/opt/vllm")]
    [InlineData(@"\\WSL.LOCALHOST\Debian", "Debian", "/")]
    [InlineData("//wsl.localhost/Ubuntu/home/me", "Ubuntu", "/home/me")]
    public void ParseWslPath_ReadsDistroAndLinuxPath(string path, string distro, string linux)
    {
        Assert.Equal(new WslLocation(distro, linux), VllmLayout.ParseWslPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\vllm")]
    [InlineData("/opt/vllm")]
    [InlineData(@"\\server\share\vllm")]
    public void ParseWslPath_IgnoresEverythingElse(string? path)
    {
        Assert.Null(VllmLayout.ParseWslPath(path));
    }

    [Fact]
    public void WslLocation_RoundTripsThroughItsUncPath()
    {
        var location = new WslLocation("Ubuntu", "/home/me/.llm-router/engines/vllm-v1.0-cuda");

        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\.llm-router\engines\vllm-v1.0-cuda", location.ToUncPath());
        Assert.Equal(location, VllmLayout.ParseWslPath(location.ToUncPath()));
    }

    [Fact]
    public void Resolve_Wsl_LaunchesThroughWslExe_WithNoShellInvolved()
    {
        var command = VllmLayout.Resolve(@"\\wsl.localhost\Ubuntu\home\me\vllm", targetIsWindows: true);

        Assert.Equal("wsl.exe", command.Executable);
        Assert.Equal(["-d", "Ubuntu", "--cd", "/home/me/vllm", "--exec", "/home/me/vllm/.venv/bin/vllm"], command.PrefixArgs);
        Assert.Null(command.WorkingDirectory);
    }

    [Fact]
    public void Resolve_Linux_RunsTheVenvExecutableFromTheInstallFolder()
    {
        var command = VllmLayout.Resolve("/opt/vllm", targetIsWindows: false);

        Assert.Equal("/opt/vllm/.venv/bin/vllm", command.Executable);
        Assert.Equal("/opt/vllm", command.WorkingDirectory);
        Assert.Empty(command.PrefixArgs);
        Assert.Null(command.Wsl);
    }

    [Fact]
    public void Resolve_NativeWindowsFolder_IsTheCommunityForkVenv()
    {
        var command = VllmLayout.Resolve(@"C:\vllm", targetIsWindows: true);

        Assert.Equal(@"C:\vllm\.venv\Scripts\vllm.exe", command.Executable);
        Assert.Null(command.Wsl);
    }

    [Fact]
    public void Resolve_FindsAVenvThatIsTheFolderItself()
    {
        var command = VllmLayout.Resolve("/opt/env", targetIsWindows: false, exists: p => p == "/opt/env/bin/vllm");

        Assert.Equal("/opt/env/bin/vllm", command.Executable);
    }

    [Theory]
    [InlineData("0.20.1", "0.20.1")]
    [InlineData("v0.20.1", "0.20.1")]
    [InlineData(" 0.20.1rc2 ", "0.20.1rc2")]
    [InlineData("1.0", "1.0")]
    public void NormalizeVersion_AcceptsVersions(string input, string expected)
    {
        Assert.Equal(expected, VllmLayout.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("0.20.1; rm -rf /")]
    [InlineData("0.20.1 --index-url http://evil")]
    [InlineData("$(whoami)")]
    public void NormalizeVersion_RejectsAnythingElse(string input)
    {
        Assert.Throws<InvalidOperationException>(() => VllmLayout.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("""{"info":{"version":"0.33.0"}}""", "v0.33.0")]
    [InlineData("""{"info":{"version":"not a version"}}""", null)]
    [InlineData("""{"message":"Not Found"}""", null)]
    public void Releases_ReadTheNewestVersionFromPyPi(string json, string? expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, VllmReleases.ParseLatestTag(doc.RootElement));
    }

    [Fact]
    public void InstallBackends_WindowsOffersCudaOnly()
    {
        Assert.Equal([BackendType.Cuda], VllmLayout.InstallBackends(targetIsWindows: true));
        Assert.Contains(BackendType.Hip, VllmLayout.InstallBackends(targetIsWindows: false));
    }

    [Fact]
    public void Script_PinsTheVersion_AndKeepsEverythingInTheInstallFolder()
    {
        var script = VllmInstaller.BuildScript("/opt/it's here", BackendType.Cuda, "v0.20.1");

        Assert.Contains("DEST='/opt/it'\\''s here'", script);
        Assert.Contains("'vllm==0.20.1' --torch-backend=auto", script);
        Assert.Contains("UV_PYTHON_INSTALL_DIR=\"$DEST/.python\"", script);
        // Package caches go in the folder too, and are gone by the end: they were 11 GB in ~/.cache otherwise.
        Assert.Contains("XDG_CACHE_HOME=\"$DEST/.cache\"", script);
        Assert.Contains("rm -rf \"$DEST/.cache\"", script);
        Assert.DoesNotContain("\r", script);
    }

    [Fact]
    public void Script_RocmUsesVllmsOwnWheelIndex()
    {
        Assert.Contains("--extra-index-url https://wheels.vllm.ai/rocm/", VllmInstaller.BuildScript("/opt/v", BackendType.Hip, null));
    }

    [Theory]
    [InlineData(BackendType.Cpu)]
    [InlineData(BackendType.Vulkan)]
    public void Script_RefusesBackendsWithoutWheels(BackendType backend)
    {
        Assert.Throws<InvalidOperationException>(() => VllmInstaller.BuildScript("/opt/v", backend, null));
    }

    [Fact]
    public void Script_RefusesAnUnsafeVersion()
    {
        Assert.Throws<InvalidOperationException>(() => VllmInstaller.BuildScript("/opt/v", BackendType.Cuda, "1.0'; touch /tmp/x; '"));
    }

    [Fact]
    public async Task Delete_RefusesARootLevelFolderInWsl()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => VllmInstaller.DeleteAsync(@"\\wsl.localhost\Ubuntu\home"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => VllmInstaller.DeleteAsync(@"\\wsl.localhost\Ubuntu"));
    }
}
