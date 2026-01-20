using DSystem.Domain.Enums;

namespace DSystem.Domain.Entities;

public class Agent : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public Guid ModelProviderApiKeyId { get; set; }
    public string? Tools { get; set; }  // JSON array of tool method names
    public AgentType Type { get; set; } = AgentType.System;
    public string? Extra { get; set; }  // JSON object for additional data (e.g., environment variables)

    public ModelProviderApiKey? ModelProviderApiKey { get; set; }

    public ICollection<AgentflowNode> Agentflows { get; set; } = new List<AgentflowNode>();
}
