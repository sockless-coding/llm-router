using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Thin client over the public Hugging Face Hub API (https://huggingface.co/docs/hub/api).
/// Registered as a typed HttpClient (see Program.cs) with AllowAutoRedirect enabled and an
/// infinite client-level timeout — callers control cancellation via the CancellationToken so
/// multi-gigabyte downloads aren't cut off by a fixed HttpClient.Timeout.
/// </summary>
public class HuggingFaceClient : IHuggingFaceClient
{
    private const string BaseUrl = "https://huggingface.co";
    private const int CopyBufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly IModelLibrarySettingsService _settings;
    private readonly ILogger<HuggingFaceClient> _logger;

    public HuggingFaceClient(HttpClient httpClient, IModelLibrarySettingsService settings, ILogger<HuggingFaceClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<HfModelSummary>> SearchModelsAsync(
        string query,
        HfModelFormat format,
        string? pipelineTag = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/models?search={Uri.EscapeDataString(query)}&filter={format.HubFilter()}&limit={limit}";
        if (!string.IsNullOrWhiteSpace(pipelineTag))
            url += $"&pipeline_tag={Uri.EscapeDataString(pipelineTag)}";
        using var request = await CreateRequestAsync(HttpMethod.Get, url, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<List<HfModelSummary>>(cancellationToken: ct);
        return results ?? new List<HfModelSummary>();
    }

    public async Task<HfRepoDetail?> GetRepoDetailAsync(string repoId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/models/{EscapeRepoId(repoId)}?blobs=true";
        using var request = await CreateRequestAsync(HttpMethod.Get, url, ct);
        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadFromJsonAsync<HfRepoDetail>(cancellationToken: ct);
    }

    public async Task<IReadOnlyList<HfRepoFile>> ListModelFilesAsync(string repoId, HfModelFormat format, CancellationToken ct = default)
    {
        var detail = await GetRepoDetailAsync(repoId, ct);
        if (detail is null)
            return Array.Empty<HfRepoFile>();

        return detail.Siblings
            .Where(f => f.Filename.EndsWith(format.FileExtension(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Filename)
            .ToList();
    }

    public async Task<string?> DownloadFileAsync(
        string repoId,
        string filename,
        string revision,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/{EscapeRepoId(repoId)}/resolve/{Uri.EscapeDataString(revision)}/{EscapeFilePath(filename)}";
        _logger.LogInformation("Downloading {RepoId}/{Filename}@{Revision} to {DestinationPath}", repoId, filename, revision, destinationPath);
        using var request = await CreateRequestAsync(HttpMethod.Get, url, ct);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw DownloadError(response, repoId, filename, hasToken: request.Headers.Authorization is not null);

        var totalBytes = response.Content.Headers.ContentLength;
        var resolvedSha = response.Headers.TryGetValues("x-repo-commit", out var shaValues)
            ? shaValues.FirstOrDefault()
            : null;

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
                progress?.Report(new DownloadProgress
                {
                    BytesReceived = bytesReceived,
                    TotalBytes = totalBytes,
                    Status = "downloading"
                });
            }
        }

        File.Move(tempPath, destinationPath, overwrite: true);
        return resolvedSha ?? (IsCommitSha(revision) ? revision : null);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        var token = (await _settings.GetAsync(ct)).HuggingFaceApiToken;
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>
    /// Turns a failed download response into a message the Models page can show as-is. Gated repos
    /// (most image/video models) answer 401 without a token and 403 until the token's account has
    /// accepted the license, so those get instructions instead of a bare status code.
    /// </summary>
    private HttpRequestException DownloadError(HttpResponseMessage response, string repoId, string filename, bool hasToken)
    {
        var hubMessage = response.Headers.TryGetValues("X-Error-Message", out var values) ? values.FirstOrDefault() : null;
        _logger.LogWarning("Hugging Face returned {StatusCode} for {RepoId}/{Filename}: {HubMessage}", (int)response.StatusCode, repoId, filename, hubMessage);

        var repoUrl = $"{BaseUrl}/{repoId}";
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when !hasToken =>
                $"{repoId} is a gated model. Accept its license at {repoUrl}, then add a Hugging Face API token from that account in the Model Library settings.",
            HttpStatusCode.Unauthorized =>
                "Hugging Face rejected the API token. Check the token in the Model Library settings.",
            HttpStatusCode.Forbidden =>
                $"{repoId} is a gated model and your token's account doesn't have access. Accept its license at {repoUrl} (fine-grained tokens also need \"read access to public gated repos\").",
            HttpStatusCode.NotFound =>
                $"{filename} was not found in {repoId}.",
            _ => $"Hugging Face returned {(int)response.StatusCode} ({response.ReasonPhrase}){(hubMessage is null ? "" : $": {hubMessage}")}.",
        };
        return new HttpRequestException(message, null, response.StatusCode);
    }

    private static string EscapeRepoId(string repoId) =>
        string.Join('/', repoId.Split('/').Select(Uri.EscapeDataString));

    private static string EscapeFilePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static bool IsCommitSha(string revision) =>
        revision.Length == 40 && revision.All(Uri.IsHexDigit);
}
