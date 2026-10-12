using LR.Core.Models;
using LR.Core.Services.AppUpdates;

namespace LR.Core.Tests.Services;

/// <summary>
/// The router's self-update: reading release versions, picking the archive that matches the
/// install, and swapping files in place with rollback.
/// </summary>
public sealed class AppUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("app-update-").FullName;
    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "target");
    private string Backup => Path.Combine(_root, "target", "data", "updates", "backup");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("2026.10.0", "2026.10.0.0")]
    [InlineData("v2026.9.3", "2026.9.3.0")]
    [InlineData("2026.10", "2026.10.0.0")]
    [InlineData("2026.10.1+3f2a9c1", "2026.10.1.0")]
    [InlineData("1.0.0-preview", "1.0.0.0")]
    public void TryParseVersion_NormalizesTagsAndInformationalVersions(string text, string expected)
    {
        Assert.True(AppRelease.TryParseVersion(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("b6543")]
    [InlineData("latest")]
    public void TryParseVersion_RejectsNonVersions(string? text)
    {
        Assert.False(AppRelease.TryParseVersion(text, out _));
    }

    [Fact]
    public void PickLatest_SkipsDraftsPrereleasesAndUnparseableTags()
    {
        var releases = new[]
        {
            new GitHubRelease { TagName = "2026.11.0", Draft = true },
            new GitHubRelease { TagName = "2026.10.1", Prerelease = true },
            new GitHubRelease { TagName = "nightly" },
            new GitHubRelease { TagName = "2026.9.3" },
            new GitHubRelease { TagName = "2026.10.0" },
        };

        var latest = AppRelease.PickLatest(releases);

        Assert.NotNull(latest);
        Assert.Equal("2026.10.0", latest.Value.Release.TagName);
    }

    [Theory]
    [InlineData(false, "llm-router-2026.10.0-win-x64.zip")]
    [InlineData(true, "llm-router-2026.10.0-win-x64-self-contained.zip")]
    public void SelectAsset_MatchesInstallFlavor(bool selfContained, string expected)
    {
        var release = new GitHubRelease
        {
            TagName = "2026.10.0",
            Assets =
            [
                new GitHubReleaseAsset { Name = "llm-router-2026.10.0-win-x64-self-contained.zip" },
                new GitHubReleaseAsset { Name = "llm-router-2026.10.0-win-x64.zip" },
            ],
        };

        Assert.Equal(expected, AppRelease.SelectAsset(release, selfContained)?.Name);
    }

    [Fact]
    public void Apply_ReplacesAndAddsFiles_KeepsUserConfigAndData()
    {
        Write(Target, "LR.Application.dll", "old");
        Write(Target, "appsettings.json", "user config");
        Write(Target, "data/lr.db", "database");
        Write(Target, "obsolete.dll", "left alone");

        Write(Source, "LR.Application.dll", "new");
        Write(Source, "appsettings.json", "release defaults");
        Write(Source, "wwwroot/css/site.css", "css");
        Write(Source, "data/lr.db", "must not ship");

        var written = UpdateInstaller.Apply(Source, Target, Backup);

        Assert.Equal(2, written);
        Assert.Equal("new", Read(Target, "LR.Application.dll"));
        Assert.Equal("css", Read(Target, "wwwroot/css/site.css"));
        Assert.Equal("user config", Read(Target, "appsettings.json"));
        Assert.Equal("database", Read(Target, "data/lr.db"));
        Assert.Equal("left alone", Read(Target, "obsolete.dll"));
        Assert.Equal("old", Read(Backup, "LR.Application.dll"));
    }

    [Fact]
    public void Apply_CopiesAppSettingsWhenInstallHasNone()
    {
        Write(Source, "appsettings.json", "release defaults");

        UpdateInstaller.Apply(Source, Target, Backup);

        Assert.Equal("release defaults", Read(Target, "appsettings.json"));
    }

    [Fact]
    public void Apply_ReplacesFileHeldOpenWithDeleteSharing()
    {
        // How Windows holds a running exe/dll (e.g. LR.Wrapper.exe under a live backend): it can be
        // renamed but not overwritten.
        Write(Target, "LR.Wrapper.exe", "old");
        Write(Source, "LR.Wrapper.exe", "new");

        using (new FileStream(Path.Combine(Target, "LR.Wrapper.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateInstaller.Apply(Source, Target, Backup);
        }

        Assert.Equal("new", Read(Target, "LR.Wrapper.exe"));
        Assert.Equal("old", Read(Backup, "LR.Wrapper.exe"));
    }

    [Fact]
    public void Apply_RollsBackEverythingWhenAFileCannotBeReplaced()
    {
        foreach (var name in new[] { "a.dll", "b.dll", "c.dll" })
        {
            Write(Target, name, "old " + name);
            Write(Source, name, "new " + name);
        }
        Write(Source, "added.dll", "new");

        var log = new List<string>();
        using (new FileStream(Path.Combine(Target, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => UpdateInstaller.Apply(Source, Target, Backup, log.Add));
        }

        Assert.Equal("old a.dll", Read(Target, "a.dll"));
        Assert.Equal("old b.dll", Read(Target, "b.dll"));
        Assert.Equal("old c.dll", Read(Target, "c.dll"));
        Assert.False(File.Exists(Path.Combine(Target, "added.dll")));
        Assert.Contains(log, line => line.Contains("rolling back"));
    }

    [Fact]
    public void Result_RoundTripsThroughFile()
    {
        var path = UpdateInstaller.GetResultPath(Target);
        new AppUpdateResult { Success = true, FromVersion = "2026.9.3", ToVersion = "2026.10.0", Message = "ok" }.Write(path);

        var read = AppUpdateResult.TryRead(path);

        Assert.NotNull(read);
        Assert.True(read.Success);
        Assert.Equal("2026.10.0", read.ToVersion);
        Assert.Null(AppUpdateResult.TryRead(Path.Combine(_root, "missing.json")));
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath));
}
