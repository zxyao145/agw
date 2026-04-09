using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Skills;

[Table("skill")]
public class Skill : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ContentPath { get; set; } = string.Empty;
}
