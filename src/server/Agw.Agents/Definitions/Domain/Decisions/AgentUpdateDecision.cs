using Agw.Shared.Tooling;

namespace Agw.Agents.Definitions.Domain.Decisions;

/// <summary>
/// 一次 Agent 更新请求中可以携带的字段。
/// Fields an agent update request may carry.
/// </summary>
public enum AgentUpdateField
{
    DisplayName,
    Description,
    SystemPrompt,
    ModelProviderId,
    Tools,
    McpToolServerIds,
    SkillIds,
    ConnectionIds,
    Extra,
    EnvironmentVariables,
    EnableSummary,
    SummaryModelProviderId,
    ResponseSchema,
}

/// <summary>
/// Application 校验并归一化后的 Agent 更新取值。<see cref="SpecifiedFields"/> 之外的字段没有取值，
/// 由 AgentBehavior 按 Agent 类型决定其中哪些写入实体。
/// Agent update values after Application validation and normalization. Fields outside
/// <see cref="SpecifiedFields"/> carry no value, and AgentBehavior decides per agent type which of them reach the entity.
/// </summary>
public sealed record AgentUpdateDecision
{
    public required IReadOnlySet<AgentUpdateField> SpecifiedFields { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? SystemPrompt { get; init; }

    public Guid? ModelProviderId { get; init; }

    public List<ToolValueObject>? Tools { get; init; }

    public string? Extra { get; init; }

    public Dictionary<string, string>? EnvironmentVariables { get; init; }

    public bool? EnableSummary { get; init; }

    public Guid? SummaryModelProviderId { get; init; }

    public string? ResponseSchema { get; init; }
}
