using System.Text.Json;
using System.Text.Json.Serialization;

using LR.Core.Models;
using LR.Core.Wrapper;

namespace LR.Core.Agents;

/// <summary>
/// Wire conventions for the router &lt;-&gt; agent WebSocket. The agent always dials out (so it
/// works behind NAT), authenticates with headers, and then both ends exchange
/// <see cref="AgentMessage"/>s as JSON text frames.
/// </summary>
public static class AgentProtocol
{
    /// <summary>Bumped on incompatible wire changes; the router refuses agents speaking a different one.</summary>
    public const int Version = 1;

    public const string ConnectPath = "/agent/connect";
    public const string EnrollPath = "/agent/enroll";
    public const string PackagePath = "/agent/package";
    public const string ModelFilePath = "/agent/model-file";
    public const string InstallShPath = "/agent/install.sh";
    public const string InstallPs1Path = "/agent/install.ps1";

    public const string AgentIdHeader = "X-LR-Agent-Id";
    public const string ProtocolHeader = "X-LR-Agent-Protocol";

    /// <summary>Runtime identifiers agent packages are published for.</summary>
    public static readonly string[] SupportedRids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64"];

    public static readonly JsonSerializerOptions JsonOptions = new(WrapperPipeConnection.JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AgentMessage message) =>
        JsonSerializer.Serialize(message, typeof(AgentMessage), JsonOptions);

    public static AgentMessage? Deserialize(string json) =>
        (AgentMessage?)JsonSerializer.Deserialize(json, typeof(AgentMessage), JsonOptions);

    /// <summary>The RID of the machine this code runs on, e.g. <c>linux-arm64</c>.</summary>
    public static string CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    /// <summary>Archive file name of an agent package (<c>lr-agent-linux-x64.tar.gz</c>).</summary>
    public static string PackageFileName(string rid) =>
        rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? $"lr-agent-{rid}.zip" : $"lr-agent-{rid}.tar.gz";
}

/// <summary>Body of <see cref="AgentProtocol.EnrollPath"/>: a new agent asking for an identity.</summary>
public sealed class AgentEnrollRequest
{
    public string EnrollmentToken { get; set; } = string.Empty;
    public string? MachineId { get; set; }
    public string? Hostname { get; set; }
    public string? Rid { get; set; }
}

/// <summary>Reply to <see cref="AgentEnrollRequest"/>: the identity the agent stores and connects with from now on.</summary>
public sealed class AgentEnrollResponse
{
    public Guid AgentId { get; set; }
    public string Secret { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>Hardware and usage snapshot an agent reports on connect and periodically.</summary>
public sealed class AgentSystemInfo
{
    public int ProcessorCount { get; set; }
    public long? TotalMemoryBytes { get; set; }
    public long? AvailableMemoryBytes { get; set; }
    public long? DiskTotalBytes { get; set; }
    public long? DiskFreeBytes { get; set; }
    public double? CpuPercent { get; set; }
    public TimeSpan? Uptime { get; set; }
    public List<string> IpAddresses { get; set; } = new();
    public List<AgentGpuInfo> Gpus { get; set; } = new();
}

public sealed class AgentGpuInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Vendor { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryUsedBytes { get; set; }
    public int? UtilizationPercent { get; set; }
}

/// <summary>A wrapper process alive on the agent's machine, as reported in <see cref="AgentHelloMessage"/>.</summary>
public sealed class AgentWrapperInfo
{
    public Guid InstanceId { get; set; }
    public bool ServerRunning { get; set; }
    public int? Port { get; set; }
}

/// <summary>One path in an <see cref="AgentPathsReport"/>.</summary>
public sealed class AgentPathStatus
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public long? SizeBytes { get; set; }
}

/// <summary>Outcome data of a finished job, whichever fields apply to its kind.</summary>
public sealed class AgentJobResult
{
    // engine installs
    public string? InstallPath { get; set; }
    public string? VersionTag { get; set; }
    public string? CommitSha { get; set; }
    public string? TargetOs { get; set; }
    public string? TargetArch { get; set; }
    public long? SizeBytes { get; set; }

    // models
    public string? FilePath { get; set; }
    public string? HfRevision { get; set; }
    public GgufMetadata? Metadata { get; set; }
    public string? DetectedMmprojPath { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
// agent -> router
[JsonDerivedType(typeof(AgentHelloMessage), "hello")]
[JsonDerivedType(typeof(AgentStatusMessage), "status")]
[JsonDerivedType(typeof(WrapperAttachedMessage), "wrapper-attached")]
[JsonDerivedType(typeof(WrapperAttachFailedMessage), "wrapper-attach-failed")]
[JsonDerivedType(typeof(WrapperClosedMessage), "wrapper-closed")]
[JsonDerivedType(typeof(JobProgressMessage), "job-progress")]
[JsonDerivedType(typeof(JobCompletedMessage), "job-completed")]
[JsonDerivedType(typeof(AgentPathsReport), "paths-report")]
[JsonDerivedType(typeof(AgentLogMessage), "log")]
// both directions
[JsonDerivedType(typeof(WrapperFrameMessage), "wrapper-frame")]
// router -> agent
[JsonDerivedType(typeof(AgentWelcomeMessage), "welcome")]
[JsonDerivedType(typeof(AttachWrapperMessage), "attach-wrapper")]
[JsonDerivedType(typeof(InstallEngineJob), "install-engine")]
[JsonDerivedType(typeof(DownloadModelJob), "download-model")]
[JsonDerivedType(typeof(PullModelJob), "pull-model")]
[JsonDerivedType(typeof(ReadModelMetadataJob), "read-model-metadata")]
[JsonDerivedType(typeof(DeletePathJob), "delete-path")]
[JsonDerivedType(typeof(CancelJobMessage), "cancel-job")]
[JsonDerivedType(typeof(CheckPathsMessage), "check-paths")]
[JsonDerivedType(typeof(UpdateAgentMessage), "update-agent")]
[JsonDerivedType(typeof(RestartAgentMessage), "restart-agent")]
public abstract class AgentMessage
{
}

// ==================== AGENT -> ROUTER ====================

/// <summary>First message on every connection: who and what the agent is, and which wrappers it already runs.</summary>
public sealed class AgentHelloMessage : AgentMessage
{
    public int ProtocolVersion { get; set; } = AgentProtocol.Version;
    public string Version { get; set; } = string.Empty;
    public string Rid { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string? OsDescription { get; set; }
    public string DataDirectory { get; set; } = string.Empty;
    public AgentSystemInfo SystemInfo { get; set; } = new();
    public List<AgentWrapperInfo> Wrappers { get; set; } = new();

    /// <summary>Jobs still running from before a reconnect (progress keeps flowing for these).</summary>
    public List<Guid> ActiveJobs { get; set; } = new();
}

/// <summary>Periodic usage snapshot.</summary>
public sealed class AgentStatusMessage : AgentMessage
{
    public AgentSystemInfo SystemInfo { get; set; } = new();
}

/// <summary>A wrapper is now relayed for <see cref="InstanceId"/>; <see cref="Hello"/> is its current state.</summary>
public sealed class WrapperAttachedMessage : AgentMessage
{
    public Guid InstanceId { get; set; }
    public HelloEvent Hello { get; set; } = new();
}

public sealed class WrapperAttachFailedMessage : AgentMessage
{
    public Guid InstanceId { get; set; }
    public string Error { get; set; } = string.Empty;

    /// <summary>True when nothing is wrong — there just isn't a wrapper and none was asked to be launched.</summary>
    public bool NotRunning { get; set; }
}

/// <summary>The wrapper for <see cref="InstanceId"/> went away (it shut down or crashed).</summary>
public sealed class WrapperClosedMessage : AgentMessage
{
    public Guid InstanceId { get; set; }
}

public sealed class JobProgressMessage : AgentMessage
{
    public Guid JobId { get; set; }
    public string? Phase { get; set; }
    public string? Message { get; set; }
    public string? LogLine { get; set; }
    public int? Percent { get; set; }
    public long? BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
}

public sealed class JobCompletedMessage : AgentMessage
{
    public Guid JobId { get; set; }
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public AgentJobResult Result { get; set; } = new();
}

public sealed class AgentPathsReport : AgentMessage
{
    public Guid RequestId { get; set; }
    public List<AgentPathStatus> Paths { get; set; } = new();
}

/// <summary>A noteworthy agent-side event (update applied, wrapper launch failure, …) for the agent's activity log.</summary>
public sealed class AgentLogMessage : AgentMessage
{
    public string Level { get; set; } = "info";
    public string Message { get; set; } = string.Empty;
}

// ==================== BOTH DIRECTIONS ====================

/// <summary>One wrapper-protocol message for (router→agent) or from (agent→router) the wrapper of <see cref="InstanceId"/>.</summary>
public sealed class WrapperFrameMessage : AgentMessage
{
    public Guid InstanceId { get; set; }
    public WrapperMessage Frame { get; set; } = null!;
}

// ==================== ROUTER -> AGENT ====================

/// <summary>The router's answer to <see cref="AgentHelloMessage"/>.</summary>
public sealed class AgentWelcomeMessage : AgentMessage
{
    public Guid AgentId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Agent version the router currently ships, if it has packages.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>Tokens for Hugging Face/GitHub, so downloads on the agent get the router's rate limits and gated-repo access.</summary>
    public string? HuggingFaceToken { get; set; }
    public string? GitHubToken { get; set; }

    /// <summary>How often to send <see cref="AgentStatusMessage"/>.</summary>
    public int StatusIntervalSeconds { get; set; } = 15;
}

/// <summary>
/// Start relaying the wrapper of <see cref="InstanceId"/>: connect to it if it runs, or launch it
/// when <see cref="Launch"/>. Answered with <see cref="WrapperAttachedMessage"/> or <see cref="WrapperAttachFailedMessage"/>.
/// </summary>
public sealed class AttachWrapperMessage : AgentMessage
{
    public Guid InstanceId { get; set; }
    public bool Launch { get; set; }
}

/// <summary>Base of every long-running job; progress and completion report back under <see cref="JobId"/>.</summary>
public abstract class AgentJobMessage : AgentMessage
{
    /// <summary>The engine build or model id the job is for.</summary>
    public Guid JobId { get; set; }
}

/// <summary>
/// Installs an engine on the agent: a prebuilt release (<see cref="Recipe"/> null) or a source
/// compile. Lands in <see cref="OutputDir"/>, or — with <see cref="InPlace"/> — is staged and then
/// mirrored onto the existing install there (an update).
/// </summary>
public sealed class InstallEngineJob : AgentJobMessage
{
    public ServerEngine Engine { get; set; }
    public BackendType Backend { get; set; }
    public string? ReleaseTag { get; set; }
    public LlamaCppBuildRecipe? Recipe { get; set; }
    public string? GitRef { get; set; }
    public string OutputDir { get; set; } = string.Empty;
    public bool InPlace { get; set; }
}

/// <summary>Downloads a Hugging Face file (every shard, for a split GGUF) straight onto the agent.</summary>
public sealed class DownloadModelJob : AgentJobMessage
{
    public string RepoId { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string Revision { get; set; } = "main";
    public string DestinationPath { get; set; } = string.Empty;
}

/// <summary>Copies a model file from the router's own library to the agent (fetched from <see cref="AgentProtocol.ModelFilePath"/>).</summary>
public sealed class PullModelJob : AgentJobMessage
{
    /// <summary>The router-side library entry to copy.</summary>
    public Guid SourceModelId { get; set; }

    /// <summary>File names of every shard, in order, as served under the source model.</summary>
    public List<string> Files { get; set; } = new();
    public long? TotalBytes { get; set; }
    public string DestinationPath { get; set; } = string.Empty;
}

/// <summary>Reads GGUF metadata of a file on the agent (a refresh, or a manual import).</summary>
public sealed class ReadModelMetadataJob : AgentJobMessage
{
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>Deletes files or folders on the agent (an engine install, every shard of a model).</summary>
public sealed class DeletePathJob : AgentJobMessage
{
    public List<string> Paths { get; set; } = new();
}

public sealed class CancelJobMessage : AgentMessage
{
    public Guid JobId { get; set; }
}

/// <summary>Asks which of <see cref="Paths"/> exist (reconciling engines/models with the agent's disk).</summary>
public sealed class CheckPathsMessage : AgentMessage
{
    public Guid RequestId { get; set; }
    public List<string> Paths { get; set; } = new();
}

/// <summary>Download <see cref="Version"/> of the agent from the router, verify it, swap it in and restart.</summary>
public sealed class UpdateAgentMessage : AgentMessage
{
    public string Version { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>Restart the agent process (backends keep running in their wrappers).</summary>
public sealed class RestartAgentMessage : AgentMessage
{
}
