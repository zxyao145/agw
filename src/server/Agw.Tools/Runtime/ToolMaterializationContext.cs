using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Agents.AI;

namespace Agw.Tools.Runtime;

public sealed class ToolMaterializationContext
{
    public required Agent Agent { get; init; }

    public required Project Project { get; init; }

    public Guid AgentId => Agent.Id;

    public Guid ProjectId => Project.Id;

    /// <summary>
    /// Identifies the persisted conversation context that owns stateful Tool Block data.
    /// </summary>
    public Guid ConversationId { get; init; }

    public required string Workspace { get; init; }

    public required string DefaultMode { get; init; }

    public IReadOnlySet<string> EnabledToolBlockNames { get; internal set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<AIAgent> BackgroundAgents { get; init; } = [];

    public Func<IReadOnlyList<Guid>, CancellationToken, ValueTask<IReadOnlyList<AIAgent>>>?
        BackgroundAgentFactory
    { get; init; }

    public bool SupportsHostedWebSearch { get; init; }

    /// <summary>
    /// 指示是否把人机交互工具转换为审批边界，使 durable runtime 能先持久化 Tool 调用再等待回答。
    /// </summary>
    public bool DeferHumanInteractions { get; init; }
}
