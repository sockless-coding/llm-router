using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Represents a log entry for a server instance, persisted to the database.
/// </summary>
[Table("ServerLogs")]
public class ServerLog
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The server instance this log entry belongs to.
    /// </summary>
    [Required, ForeignKey(nameof(ServerInstance))]
    public Guid ServerInstanceId { get; set; }

    /// <summary>
    /// When the log entry was created.
    /// </summary>
    [Required]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The severity level of this log entry.
    /// </summary>
    [Required, MaxLength(16)]
    public string Level { get; set; } = "Info";

    /// <summary>
    /// The log message content.
    /// </summary>
    [Required, MaxLength(4096)]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Navigation: the server instance this log belongs to.
    /// </summary>
    public ServerInstance? ServerInstance { get; set; }
}
