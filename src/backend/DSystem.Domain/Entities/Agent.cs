using DSystem.Shared;
using DSystem.Shared.Enums;

namespace DSystem.Domain.Entities;

public class Agent : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Model Provider API Key ID. Required for System agents, optional for External agents.
    /// </summary>
    public Guid? ModelProviderApiKeyId { get; set; }

    public string? Tools { get; set; }  // JSON array of tool method names
    public AgentType Type { get; set; } = AgentType.System;

    /// <summary>
    /// JSON object for additional data (e.g., environment variables).
    /// </summary>
    public string? Extra { get; set; }

    public ModelProviderApiKey? ModelProviderApiKey { get; set; }

    public ICollection<AgentflowNode> Agentflows { get; set; } = new List<AgentflowNode>();
}
