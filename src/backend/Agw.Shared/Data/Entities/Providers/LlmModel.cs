using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Contracts.Providers;

namespace Agw.Shared.Data.Entities.Providers;

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
