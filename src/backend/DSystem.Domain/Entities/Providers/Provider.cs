using DSystem.Shared;

namespace DSystem.Domain.Entities;

public class Provider : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<ModelProvider> Models { get; set; } = new List<ModelProvider>();
}
