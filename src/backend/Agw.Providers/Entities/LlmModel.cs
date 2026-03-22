using Agw.Shared;
using Agw.Shared.Abstractions;
using Agw.Shared.Enums;

namespace Agw.Domain.Entities;

public class LlmModel : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ModelType Type { get; set; }
    public int MaxTokens { get; set; }

    public ICollection<ModelProvider> Providers { get; set; } = new List<ModelProvider>();
}
