using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

/// <summary>
/// Strata checkouts in the engine-build registry: reading a checkout's versions and models, the
/// reconcile pass moving it between NeedsSetup / Ready / Missing, update checks against the
/// right repo and branch, and removal never touching the checkout's files.
/// </summary>
public sealed class StrataInstallTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("strata-install-").FullName;
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly StubGitHub _github = new();
    private readonly EngineBuildManager _manager;

    public StrataInstallTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _manager = new EngineBuildManager(_context, _github, [new LlamaCppInstallHandler(), new StrataInstallHandler()]);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private void Write(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A checkout as cloned, before Strata's first-time setup.</summary>
    private void CreateClone(string version = "0.1.40")
    {
        Write(StrataLayout.ServerScriptRelativePath);
        Write(StrataLayout.SetupScript);
        Write("CMakeLists.txt", $"cmake_minimum_required(VERSION 3.24)\nproject(strata VERSION {version} LANGUAGES C CXX)\n");
    }

    private void RunSetup()
    {
        Write(StrataLayout.PythonRelativePath);
        Write(Path.Combine("engine", "BUILD.json"), """{ "version": "0.1.39" }""");
        Write("strata-coder.json", """{ "exe": "engine/strata.exe", "args": [], "model_name": "Coder" }""");
        Write("strata-amd.json", """{ "exe": "engine/strata.exe", "args": [], "backend": "hip" }""");
        // Not a run config (no exe/args) — must be ignored.
        Write("strata-coder.shared-settings.json", """{ "temperature": 0.6 }""");
    }

    private async Task<EngineBuild> AddRowAsync(EngineBuildStatus status, string? commit = null,
        EngineBuildSource source = EngineBuildSource.GitCheckout, string? versionTag = null)
    {
        var build = new EngineBuild
        {
            Name = "Strata",
            Engine = ServerEngine.Strata,
            Source = source,
            VersionTag = versionTag,
            InstallPath = _root,
            Status = status,
            CommitSha = commit,
        };
        _context.EngineBuilds.Add(build);
        await _context.SaveChangesAsync();
        return build;
    }

    [Fact]
    public void Layout_ReadsVersionsAndRunConfigs()
    {
        CreateClone("0.1.40");
        Assert.True(StrataLayout.IsCheckout(_root));
        Assert.False(StrataLayout.IsSetUp(_root));

        RunSetup();

        Assert.True(StrataLayout.IsSetUp(_root));
        Assert.Equal("0.1.40", StrataLayout.ReadSourceVersion(_root));
        Assert.Equal("0.1.39", StrataLayout.ReadEngineVersion(_root));
        var configs = StrataLayout.FindRunConfigs(_root);
        Assert.Equal(["strata-amd.json", "strata-coder.json"], configs.Select(c => c.FileName));
        Assert.Equal("Coder", configs.Single(c => c.FileName == "strata-coder.json").ModelName);
        Assert.Equal("amd", configs.Single(c => c.FileName == "strata-amd.json").ModelName);
        Assert.Equal(BackendType.Hip, StrataInstallInfo.DetectBackend(_root));
    }

    [Fact]
    public async Task Reconcile_TracksSetupStateAndVersion()
    {
        CreateClone("0.1.40");
        var build = await AddRowAsync(EngineBuildStatus.Ready);

        await _manager.ReconcileAsync();
        Assert.Equal(EngineBuildStatus.NeedsSetup, build.Status);
        Assert.Equal("v0.1.40", build.VersionTag);

        // A .venv alone isn't set up: no model has been configured yet.
        Write(StrataLayout.PythonRelativePath);
        await _manager.ReconcileAsync();
        Assert.Equal(EngineBuildStatus.NeedsSetup, build.Status);

        RunSetup();
        await _manager.ReconcileAsync();
        Assert.Equal(EngineBuildStatus.Ready, build.Status);

        File.Delete(Path.Combine(_root, StrataLayout.ServerScriptRelativePath));
        await _manager.ReconcileAsync();
        Assert.Equal(EngineBuildStatus.Missing, build.Status);
    }

    [Fact]
    public async Task UpdateStatus_ComparesCommitWithStrataMain()
    {
        CreateClone();
        RunSetup();
        var sha = new string('a', 40);
        var build = await AddRowAsync(EngineBuildStatus.Ready, sha);
        _github.Compare = new GitHubCompareResult { Status = "behind", AheadBy = 3 };

        var status = await _manager.GetUpdateStatusAsync(build.Id);

        Assert.Null(status.Error);
        Assert.Equal((StrataLayout.Repo, sha, StrataLayout.Branch), _github.LastCompare);
        Assert.True(status.UpdateAvailable);
        Assert.Equal(3, status.BehindBy);
    }

    [Fact]
    public async Task UpdateStatus_ReleaseInstallComparesWithLatestRelease()
    {
        CreateClone("0.1.40");
        RunSetup();
        var build = await AddRowAsync(EngineBuildStatus.Ready, new string('b', 40), EngineBuildSource.OfficialRelease, "v0.1.40.4");
        _github.LatestRelease = new GitHubRelease { TagName = "v0.1.41" };
        _github.Compare = new GitHubCompareResult { Status = "behind", AheadBy = 12 };

        var status = await _manager.GetUpdateStatusAsync(build.Id);

        Assert.Null(status.Error);
        Assert.Equal((StrataLayout.Repo, "v0.1.40.4", "v0.1.41"), _github.LastCompare);
        Assert.True(status.UpdateAvailable);
    }

    [Fact]
    public async Task Reconcile_KeepsReleaseInstallsTag()
    {
        CreateClone("0.1.40");
        RunSetup();
        var build = await AddRowAsync(EngineBuildStatus.Ready, source: EngineBuildSource.OfficialRelease, versionTag: "v0.1.40.4");

        await _manager.ReconcileAsync();

        Assert.Equal("v0.1.40.4", build.VersionTag);
        Assert.Equal(EngineBuildStatus.Ready, build.Status);
    }

    [Theory]
    [InlineData("v0.1.41", true)]
    [InlineData("v0.1.40.4", true)]
    [InlineData("main", false)]
    [InlineData("v0.1.41-rc1", false)]
    [InlineData("0.1.41", false)]
    public void ReleaseTags(string tag, bool isRelease) => Assert.Equal(isRelease, StrataLayout.IsReleaseTag(tag));

    [Fact]
    public async Task Remove_KeepsCheckoutAndServersFolder()
    {
        CreateClone();
        RunSetup();
        var build = await AddRowAsync(EngineBuildStatus.Ready);
        var server = new ServerInstance { Name = "s", Engine = ServerEngine.Strata, Config = new BackendConfig { EngineBuildId = build.Id } };
        _context.ServerInstances.Add(server);
        await _context.SaveChangesAsync();

        Assert.True(await _manager.DeleteBuildAsync(build.Id, deleteFiles: true));

        Assert.True(StrataLayout.IsSetUp(_root));
        Assert.Null(server.Config.EngineBuildId);
        Assert.Equal(_root, server.Config.InstallFolderPath);
    }

    private sealed class StubGitHub : IGitHubClient
    {
        public GitHubCompareResult? Compare { get; set; }
        public GitHubRelease? LatestRelease { get; set; }
        public (string Repo, string Base, string Head)? LastCompare { get; private set; }

        public Task<GitHubCompareResult?> CompareAsync(string repo, string baseRef, string headRef, CancellationToken ct = default)
        {
            LastCompare = (repo, baseRef, headRef);
            return Task.FromResult(Compare);
        }

        public Task<GitHubRelease?> GetLatestReleaseAsync(string repo, CancellationToken ct = default) => Task.FromResult(LatestRelease);
        public Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string tag, CancellationToken ct = default) => Task.FromResult<GitHubRelease?>(null);
        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string repo, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GitHubRelease>>([]);
        public Task<string?> GetRawFileAsync(string repo, string reference, string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DownloadAssetAsync(string downloadUrl, string destinationPath, IProgress<EngineBuildProgress>? progress, CancellationToken ct = default) => Task.CompletedTask;
    }
}
