using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Domain.ValueObjects;

/// <summary>
/// <para>用户可以设置的任务定义；名称已经由调用方解析为最终取值。</para>
/// <para>The job definition a user can set; the name is already resolved to its final value by the caller.</para>
/// </summary>
public sealed record JobDefinition
{
    public required Guid ProjectId { get; init; }

    public AgentRuntimeType? AgentType { get; init; }

    public Guid? AgentId { get; init; }

    public required string Name { get; init; }

    public string? Prompt { get; init; }

    public required TriggerType TriggerType { get; init; }

    public required string TriggerValue { get; init; }

    public required int MaxRetryCount { get; init; }

    public required bool IsEnabled { get; init; }
}
