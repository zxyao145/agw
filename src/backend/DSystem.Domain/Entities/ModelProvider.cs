namespace DSystem.Domain.Entities;

public class ModelProvider : BaseEntity
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
    public ICollection<ModelProviderApiKey> ApiKeys { get; set; } = new List<ModelProviderApiKey>();
}
