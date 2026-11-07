using System.Text.Json;
using System.Text.Json.Serialization;

namespace LR.Agent;

/// <summary>
/// The agent's on-disk settings (<c>agent.json</c> next to the executable, or <c>--config</c>).
/// The install script writes <see cref="RouterUrl"/> and <see cref="EnrollmentToken"/>; the agent
/// swaps the token for its own identity on first contact and rewrites the file.
/// </summary>
public sealed class AgentConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string RouterUrl { get; set; } = string.Empty;

    /// <summary>Only present until enrollment succeeds (or when an install script re-adds it to re-enroll).</summary>
    public string? EnrollmentToken { get; set; }

    public Guid? AgentId { get; set; }
    public string? Secret { get; set; }

    /// <summary>Generated once per install; lets the router recognise a reinstall on the same machine.</summary>
    public string MachineId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Where engines, models, wrapper state and logs live. Defaults to <c>data</c> next to the executable.</summary>
    public string? DataDirectory { get; set; }

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string ResolvedDataDirectory => Path.GetFullPath(
        string.IsNullOrWhiteSpace(DataDirectory) ? Path.Combine(AppContext.BaseDirectory, "data") : DataDirectory);

    [JsonIgnore]
    public bool IsEnrolled => AgentId is not null && !string.IsNullOrEmpty(Secret);

    public static AgentConfig Load(string path)
    {
        AgentConfig config;
        if (File.Exists(path))
        {
            config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOptions) ?? new AgentConfig();
        }
        else
        {
            config = new AgentConfig();
        }
        config.FilePath = path;

        // Environment overrides (handy for containers and quick tests).
        if (Environment.GetEnvironmentVariable("LR_AGENT_ROUTER_URL") is { Length: > 0 } url) config.RouterUrl = url;
        if (Environment.GetEnvironmentVariable("LR_AGENT_ENROLLMENT_TOKEN") is { Length: > 0 } token) config.EnrollmentToken = token;
        if (Environment.GetEnvironmentVariable("LR_AGENT_DATA_DIR") is { Length: > 0 } dir) config.DataDirectory = dir;
        return config;
    }

    /// <summary>Writes the file atomically (temp + move), owner-readable only on Unix since it holds the secret.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, FilePath, overwrite: true);
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "agent.json");
}
