namespace DSystem.Domain.Entities;

public class ModelProviderApiKey : BaseEntity
{
    public Guid Id { get; set; }
    public Guid ModelProviderId { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool Enable { get; set; } = true;

    public ModelProvider? ModelProvider { get; set; }
    public ICollection<Agent> Agents { get; set; } = new List<Agent>();
}

