using Agw.Shared.Tooling;

namespace Agw.Agents.Definitions.Domain.ValueObjects;

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
/// 一次 Agent 更新携带的取值：Extra 与 ResponseSchema 的 JSON 已由 Application 归一化，<see cref="SpecifiedFields"/> 之外的字段没有取值，
/// 由 AgentBehavior 按 Agent 类型校验并决定哪些字段写入实体。
/// The values of one agent update: Application has normalized the Extra and ResponseSchema JSON, fields outside
/// <see cref="SpecifiedFields"/> carry no value, and AgentBehavior validates them per agent type and decides which reach the entity.
/// </summary>
public sealed record AgentUpdate
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
