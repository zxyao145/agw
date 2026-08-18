using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Providers;

[Table("provider")]
[EntityTypeConfiguration(typeof(ProviderConfiguration))]
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
