namespace DSystem.Domain.Entities;

public class Agent : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public Guid ModelProviderApiKeyId { get; set; }

    public ModelProviderApiKey? ModelProviderApiKey { get; set; }

    public ICollection<WorkflowNode> Workflows { get; set; } = new List<WorkflowNode>();
}
