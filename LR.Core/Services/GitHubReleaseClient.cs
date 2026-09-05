using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Thin client over the public GitHub REST API (https://docs.github.com/rest). Registered as a
/// typed HttpClient (see Program.cs) with an infinite client-level timeout — callers control
/// cancellation via the CancellationToken so large asset downloads aren't cut off. Mirrors
/// <see cref="HuggingFaceClient"/>.
/// </summary>
public class GitHubReleaseClient : IGitHubClient
{
    private const string ApiBaseUrl = "https://api.github.com";
    private const int CopyBufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly IEngineBuildSettingsService _settings;
    private readonly ILogger<GitHubReleaseClient> _logger;

    public GitHubReleaseClient(HttpClient httpClient, IEngineBuildSettingsService settings, ILogger<GitHubReleaseClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(string repo, CancellationToken ct = default)
    {
        // llama.cpp publishes its per-commit "b####" builds as GitHub *prereleases*, so
        // /releases/latest (which ignores prereleases) points at an unrelated tag. Take the newest
        // release from the list whose tag looks like a build number instead.
        var releases = await ListReleasesAsync(repo, 30, ct);
        var build = releases.FirstOrDefault(r =>
            System.Text.RegularExpressions.Regex.IsMatch(r.TagName, @"^b\d{3,}$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return build ?? releases.FirstOrDefault(r => !r.Prerelease) ?? releases.FirstOrDefault();
    }

    public async Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string tag, CancellationToken ct = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{ApiBaseUrl}/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string repo, int limit = 20, CancellationToken ct = default)
    {
        var perPage = Math.Clamp(limit, 1, 100);
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{ApiBaseUrl}/repos/{repo}/releases?per_page={perPage}", ct);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: ct);
        return releases ?? new List<GitHubRelease>();
    }

    public async Task<GitHubCompareResult?> CompareAsync(string repo, string baseRef, string headRef, CancellationToken ct = default)
    {
        // GitHub's compare endpoint takes "base...head" as a single path segment.
        var slug = $"{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(headRef)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, $"{ApiBaseUrl}/repos/{repo}/compare/{slug}", ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GitHub compare {Base}...{Head} on {Repo} failed: {Status}", baseRef, headRef, repo, response.StatusCode);
            return null;
        }
        return await response.Content.ReadFromJsonAsync<GitHubCompareResult>(cancellationToken: ct);
    }

    public async Task DownloadAssetAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<EngineBuildProgress>? progress,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Downloading engine asset {Url} to {DestinationPath}", downloadUrl, destinationPath);
        using var request = await CreateRequestAsync(HttpMethod.Get, downloadUrl, ct);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempPath = destinationPath + ".part";

        long bytesReceived = 0;
        await using (var httpStream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
        {
            var buffer = new byte[CopyBufferSize];
            int bytesRead;
            while ((bytesRead = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                bytesReceived += bytesRead;
                progress?.Report(new EngineBuildProgress
                {
                    Phase = "download",
                    Percent = totalBytes is > 0 ? (int)(bytesReceived * 100 / totalBytes.Value) : null,
                    Message = totalBytes is > 0
                        ? $"Downloading… {bytesReceived / 1024 / 1024} / {totalBytes.Value / 1024 / 1024} MB"
                        : $"Downloading… {bytesReceived / 1024 / 1024} MB",
                    Status = "running",
                });
            }
        }

        File.Move(tempPath, destinationPath, overwrite: true);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        // GitHub rejects API requests without a User-Agent.
        request.Headers.UserAgent.ParseAdd("llm-router");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        var token = (await _settings.GetAsync(ct)).GitHubApiToken;
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }
}
