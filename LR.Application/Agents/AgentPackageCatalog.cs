using System.Text.Json;

using LR.Core.Agents;

namespace LR.Application.Agents;

/// <summary><c>manifest.json</c> of the agent packages this router ships (written by the packaging script).</summary>
public sealed class AgentPackageManifest
{
    public string Version { get; set; } = string.Empty;
    public DateTime? BuiltAt { get; set; }
    public List<AgentPackageEntry> Packages { get; set; } = new();
}

public sealed class AgentPackageEntry
{
    public string Rid { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>
/// The agent builds the router hands out — to install scripts and to agents updating themselves.
/// Read from <c>agent-packages/</c> next to the app (or <c>Agents:PackageFolder</c>); release
/// archives include it, and <c>publish-agents.ps1</c> produces it for a dev build.
/// </summary>
public sealed class AgentPackageCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _folder;
    private readonly object _lock = new();
    private AgentPackageManifest? _cached;
    private DateTime _cachedStamp;

    public AgentPackageCatalog(IConfiguration configuration)
    {
        var configured = configuration["Agents:PackageFolder"];
        _folder = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "agent-packages")
            : configured);
    }

    public string Folder => _folder;

    /// <summary>The current manifest, or null when no packages are present.</summary>
    public AgentPackageManifest? GetManifest()
    {
        var path = Path.Combine(_folder, "manifest.json");
        if (!File.Exists(path)) return null;

        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_lock)
        {
            if (_cached is not null && stamp == _cachedStamp) return _cached;
            try
            {
                _cached = JsonSerializer.Deserialize<AgentPackageManifest>(File.ReadAllText(path), JsonOptions);
                _cachedStamp = stamp;
            }
            catch
            {
                _cached = null;
            }
            return _cached;
        }
    }

    public string? LatestVersion => GetManifest()?.Version;

    /// <summary>The package for <paramref name="rid"/> and its file on disk, if it's there.</summary>
    public (AgentPackageEntry Entry, string Path)? Find(string? rid)
    {
        if (string.IsNullOrWhiteSpace(rid)) return null;
        var entry = GetManifest()?.Packages.FirstOrDefault(p => p.Rid.Equals(rid, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        var file = Path.Combine(_folder, Path.GetFileName(entry.File));
        return File.Exists(file) ? (entry, file) : null;
    }

    /// <summary>Which supported RIDs have a package.</summary>
    public IReadOnlyList<(string Rid, AgentPackageEntry? Entry)> Availability() =>
        AgentProtocol.SupportedRids.Select(r => (r, Find(r)?.Entry)).ToList();
}
