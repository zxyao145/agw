using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Providers;

[Table("model_provider_relation")]
[EntityTypeConfiguration(typeof(ModelProviderRelationConfiguration))]
public class ModelProviderRelation : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public Guid ModelId { get; set; }
    public Guid ProviderId { get; set; }

    public decimal InputPrice { get; set; }
    public decimal OutputPrice { get; set; }
    public decimal CacheRead { get; set; }
    public decimal CacheWrite { get; set; }
    public int RpsLimit { get; set; }

    public LlmModel? Model { get; set; }
    public Provider? Provider { get; set; }
}
