using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data;
using Agw.Shared.Enums;

namespace Agw.Providers.Domain.Entities;

[Table("model")]
public class LlmModel : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ModelType Type { get; set; }
    public int MaxTokens { get; set; }

    public ICollection<ModelProviderRelation> Providers { get; set; } = new List<ModelProviderRelation>();
}
