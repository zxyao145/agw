using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Skills;

[Table("remote_skill_cache")]
[EntityTypeConfiguration(typeof(RemoteSkillCacheConfiguration))]
public class RemoteSkillCache
{
    public Guid SkillId { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string ContentJson { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
}
