using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Join row scoping an <see cref="ApiKey"/> to a single allowed <see cref="ModelPreset"/>.
/// Only consulted when the key's <see cref="ApiKey.AllowAllModels"/> is false.
/// </summary>
[Table("ApiKeyModelPresets")]
public class ApiKeyModelPreset
{
    [ForeignKey(nameof(ApiKey))]
    public Guid ApiKeyId { get; set; }

    public ApiKey? ApiKey { get; set; }

    [ForeignKey(nameof(ModelPreset))]
    public Guid ModelPresetId { get; set; }

    public ModelPreset? ModelPreset { get; set; }
}
