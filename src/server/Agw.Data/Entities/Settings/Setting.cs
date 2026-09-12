using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Settings;

[Table("setting")]
[EntityTypeConfiguration(typeof(SettingConfiguration))]
public sealed class Setting : BaseEntity
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? UserId { get; set; }

    [JsonIgnore]
    public string ValueJson { get; set; } = string.Empty;
    public long Version { get; set; }
}
