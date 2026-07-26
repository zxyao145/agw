using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Abstractions;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Providers;

[Table("model")]
[EntityTypeConfiguration(typeof(LlmModelConfiguration))]
public class LlmModel : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int MaxTokens { get; set; }

    public ICollection<ModelProviderRelation> Providers { get; set; } = new List<ModelProviderRelation>();
}
