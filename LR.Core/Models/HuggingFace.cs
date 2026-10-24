using System.Text.Json.Serialization;

namespace LR.Core.Models;

/// <summary>
/// The model file format the Hugging Face browser searches for: GGUF (llama.cpp, Strata,
/// stable-diffusion.cpp) or safetensors (the format most diffusion checkpoints, VAEs and text
/// encoders ship in, which stable-diffusion.cpp loads as is).
/// </summary>
public enum HfModelFormat
{
    Gguf,
    Safetensors,
}

public static class HfModelFormatExtensions
{
    /// <summary>The hub's library filter for repos containing this format (<c>?filter=</c>).</summary>
    public static string HubFilter(this HfModelFormat format) => format switch
    {
        HfModelFormat.Safetensors => "safetensors",
        _ => "gguf",
    };

    public static string FileExtension(this HfModelFormat format) => format switch
    {
        HfModelFormat.Safetensors => ".safetensors",
        _ => ".gguf",
    };

    public static string DisplayName(this HfModelFormat format) => format switch
    {
        HfModelFormat.Safetensors => "Safetensors",
        _ => "GGUF",
    };
}

/// <summary>
/// A single search result from the Hugging Face model hub.
/// </summary>
public class HfModelSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("likes")]
    public long Likes { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// A single file (sibling) within a Hugging Face repo.
/// </summary>
public class HfRepoFile
{
    [JsonPropertyName("rfilename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long? SizeBytes { get; set; }
}

/// <summary>
/// Full repo detail — used both to list GGUF files and to read the current revision (for
/// "check for updates").
/// </summary>
public class HfRepoDetail
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("likes")]
    public long Likes { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("library_name")]
    public string? LibraryName { get; set; }

    [JsonPropertyName("pipeline_tag")]
    public string? PipelineTag { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("cardData")]
    public HfCardData? CardData { get; set; }

    [JsonPropertyName("siblings")]
    public List<HfRepoFile> Siblings { get; set; } = new();
}

/// <summary>
/// The subset of a repo's model-card metadata (YAML front matter) we surface in the UI.
/// </summary>
public class HfCardData
{
    [JsonPropertyName("license")]
    public string? License { get; set; }
}

/// <summary>
/// Live progress for an in-flight model download, broadcast over SignalR.
/// </summary>
public class DownloadProgress
{
    public Guid ModelId { get; set; }
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public string Status { get; set; } = "downloading"; // downloading | completed | error
    public string? ErrorMessage { get; set; }

    public double? PercentComplete =>
        TotalBytes is > 0 ? Math.Round((double)BytesReceived / TotalBytes.Value * 100, 1) : null;
}
