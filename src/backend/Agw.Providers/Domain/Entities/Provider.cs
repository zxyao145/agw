using Agw.Shared;
using Agw.Shared.Abstractions;
using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Providers.Domain.Entities;

[Table("provider")]
public class Provider : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProviderType ProviderType { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<ModelProviderRelation> Models { get; set; } = new List<ModelProviderRelation>();
    public ICollection<ProviderAuthConfig> AuthConfigs { get; set; } = new List<ProviderAuthConfig>();
}
