using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Providers;

[Table("model")]
[EntityTypeConfiguration(typeof(AgwAiModelConfiguration))]
public class AgwAiModel : BaseEntity, IAggregateRoot
{
    public const int DefaultMaxContextWindowTokens = 256_000;
    public const int DefaultMaxOutputTokens = 64_000;

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MaxContextWindowTokens { get; set; } = DefaultMaxContextWindowTokens;
    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    public ICollection<ModelProviderRelation> Providers { get; set; } = new List<ModelProviderRelation>();
}
