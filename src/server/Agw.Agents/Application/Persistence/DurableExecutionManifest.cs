using System.Text.Json.Serialization;

namespace Agw.Agents.Application.Persistence;

/// <summary>
/// 一次执行的不可变启动清单。该对象以加密 JSON 保存，只包含重建 runtime 所需的最小输入。
/// </summary>
public sealed record DurableExecutionManifest
{
    /// <summary>
    /// 获取当前启动清单的序列化架构版本。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// 获取该启动清单使用的序列化架构版本。
    /// </summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// 获取本次业务执行的稳定标识。
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>
    /// 获取发起执行的稳定用户标识。旧清单缺少该字段时回退内置管理员。
    /// </summary>
    public string UserId { get; init; } = Constants.AdminUserId;

    /// <summary>
    /// 获取需要执行的 Agent 标识。
    /// </summary>
    public required Guid AgentId { get; init; }

    /// <summary>
    /// 获取 Agent 运行时类型。
    /// </summary>
    public required AgentRuntimeType AgentType { get; init; }

    /// <summary>
    /// 获取首个执行分段消费的原始用户输入。
    /// </summary>
    public required AgwUserInput Input { get; init; }

    /// <summary>
    /// 获取重建运行时所需的最小任务上下文。
    /// </summary>
    public required DurableProjectTaskSnapshot Task { get; init; }

    /// <summary>
    /// 获取重建运行时所需的执行设置。
    /// </summary>
    public required DurableExecutionSettings Settings { get; init; }

    /// <summary>
    /// 获取创建当前分支的历史 Agentflow checkpoint occurrence。
    /// </summary>
    public Guid? ResumeCheckpointOccurrenceId { get; init; }

    /// <summary>
    /// 获取恢复分支首次启动时需要自动应答的 Checkpoint RequestPort 节点。
    /// </summary>
    public IReadOnlyList<string> ResumeCheckpointNodeIds { get; init; } = [];
}

/// <summary>
/// 从完整 AgentExecutionTask 提取的最小任务上下文。
/// </summary>
public sealed record DurableProjectTaskSnapshot
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Generation { get; init; }

    /// <summary>
    /// 获取任务标识。
    /// </summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// 获取项目会话标识。
    /// </summary>
    public required Guid ProjectConversationId { get; init; }

    /// <summary>
    /// 获取项目标识。
    /// </summary>
    public required Guid ProjectId { get; init; }

    /// <summary>
    /// 获取持久化会话上下文标识。
    /// </summary>
    public required string ContextId { get; init; }
}

/// <summary>
/// 从连接设置提取的不可变 durable runtime 设置。
/// </summary>
public sealed record DurableExecutionSettings
{
    /// <summary>
    /// 获取创建 Agent runtime 时需要注入的环境变量副本。
    /// </summary>
    public required Dictionary<string, string> EnvironmentVariables { get; init; }

    /// <summary>
    /// 获取工具调用的权限模式。
    /// </summary>
    public AgwPermissionMode? PermissionMode { get; init; }

    public long PermissionVersion { get; init; }

    public AgwPermissionMode? NextPermissionMode { get; init; }

    public long NextPermissionVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public HumanInteractionPolicy HumanInteractionPolicy { get; init; } = HumanInteractionPolicy.Allow;

    /// <summary>
    /// 获取是否恢复已有 Agent 会话。
    /// </summary>
    public required bool Resume { get; init; }
}
