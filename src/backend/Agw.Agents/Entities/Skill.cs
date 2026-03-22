using Agw.Shared;

namespace Agw.Domain.Entities;

public class Skill : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ContentPath { get; set; } = string.Empty;
}
