using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 单次可恢复分段的结果类型。
/// </summary>
internal enum DurableExecutionSegmentStatus
{
    WaitingForHuman = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>
/// 一次执行的不可变启动清单。该对象以加密 JSON 保存，只包含重建 runtime 所需的最小输入。
/// </summary>
internal sealed record DurableExecutionManifest
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

    public string ResolveUserId() => string.IsNullOrWhiteSpace(UserId) ? Constants.AdminUserId : UserId;
}

/// <summary>
/// 从完整 AgentExecutionTask 提取的最小任务上下文。
/// </summary>
internal sealed record DurableProjectTaskSnapshot
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

    /// <summary>
    /// 从运行时任务投影创建可持久化的最小快照。
    /// </summary>
    public static DurableProjectTaskSnapshot FromProjection(AgentExecutionTask task) =>
        new()
        {
            TaskId = task.TaskId,
            Generation = task.Generation,
            ProjectConversationId = task.ProjectConversationId,
            ProjectId = task.ProjectId,
            ContextId = task.ContextId,
        };

    /// <summary>
    /// 重建 Agent runtime 所需的任务投影。
    /// </summary>
    public AgentExecutionTask ToProjection() =>
        new()
        {
            TaskId = TaskId,
            Generation = Generation,
            ProjectConversationId = ProjectConversationId,
            ProjectId = ProjectId,
            ContextId = ContextId,
        };
}

/// <summary>
/// 从连接设置提取的不可变 durable runtime 设置。
/// </summary>
internal sealed record DurableExecutionSettings
{
    /// <summary>
    /// 获取创建 Agent runtime 时需要注入的环境变量副本。
    /// </summary>
    public required Dictionary<string, string> EnvironmentVariables { get; init; }

    /// <summary>
    /// 获取工具调用的权限模式。
    /// </summary>
    public PermissionMode? PermissionMode { get; init; }

    /// <summary>
    /// 获取是否恢复已有 Agent 会话。
    /// </summary>
    public required bool Resume { get; init; }

    /// <summary>
    /// 创建设置快照，并规范化环境变量顺序以支持幂等比较。
    /// </summary>
    public static DurableExecutionSettings FromSettings(ExecutionSettings settings) =>
        new()
        {
            EnvironmentVariables = settings
                .EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            PermissionMode = settings.PermissionMode,
            Resume = settings.Resume,
        };

    /// <summary>
    /// 重建 Agent runtime 接受的 SettingCommand。
    /// </summary>
    public SettingCommand ToCommand(Guid projectId, string contextId) =>
        new(projectId, new Dictionary<string, string>(EnvironmentVariables), contextId, PermissionMode)
        {
            Resume = Resume,
        };
}

/// <summary>
/// 调用可恢复分段所需的输入，包括上一分段的人机回答和 Agentflow checkpoint。
/// </summary>
/// <param name="ExecutionId">当前业务执行标识。</param>
/// <param name="SegmentIndex">从零开始的分段序号。</param>
/// <param name="ResolvedInteractions">上一等待边界已解析的人工回答。</param>
/// <param name="Checkpoint">上一分段输出的 Agentflow checkpoint。</param>
internal sealed record DurableExecutionSegmentInput(
    Guid ExecutionId,
    int SegmentIndex,
    IReadOnlyList<DurableResolvedInteraction> ResolvedInteractions,
    DurableAgentflowCheckpoint? Checkpoint
);

/// <summary>
/// 分段执行器与 PostgreSQL 状态机之间的持久边界。
/// </summary>
internal sealed record DurableExecutionSegmentResult
{
    /// <summary>
    /// 获取产生该结果的业务执行标识。
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>
    /// 获取产生该结果的分段序号。
    /// </summary>
    public required int SegmentIndex { get; init; }

    /// <summary>
    /// 获取分段结束时的状态。
    /// </summary>
    public required DurableExecutionSegmentStatus Status { get; init; }

    /// <summary>
    /// 获取本分段捕获的待处理人工交互。
    /// </summary>
    public IReadOnlyList<DurableHumanInteractionSnapshot> PendingInteractions { get; init; } = [];

    /// <summary>
    /// 获取本分段生成的最新 Agentflow checkpoint。
    /// </summary>
    public DurableAgentflowCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// 获取分段失败时的错误说明。
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 可跨 Server 持久化并恢复的 Agentflow JSON checkpoint。
/// </summary>
internal sealed record DurableAgentflowCheckpoint
{
    /// <summary>
    /// 获取 checkpoint 所属的 workflow session 标识。
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 获取 checkpoint 标识。
    /// </summary>
    public required string CheckpointId { get; init; }

    /// <summary>
    /// 获取父 checkpoint 的 workflow session 标识。
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// 获取父 checkpoint 标识。
    /// </summary>
    public string? ParentCheckpointId { get; init; }

    /// <summary>
    /// 获取 MAF 生成的 checkpoint JSON 内容。
    /// </summary>
    public required JsonElement Payload { get; init; }
}

/// <summary>
/// 通过 PostgreSQL 状态机持久提交的人工回答。
/// </summary>
internal sealed record DurableHumanResponseEnvelope
{
    /// <summary>
    /// 获取回答所属的业务执行标识。
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>
    /// 获取回答对应的人工请求标识。
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// 获取用户是否批准该请求。
    /// </summary>
    public required bool Approved { get; init; }

    /// <summary>
    /// 获取用户提交的可选文本回答。
    /// </summary>
    public string? ResponseText { get; init; }

    /// <summary>
    /// 获取 Tool approval 的生效范围。
    /// </summary>
    public string ApprovalScope { get; init; } = "once";

    /// <summary>
    /// 获取结构化人工回答数据。
    /// </summary>
    public JsonElement? ResponseData { get; init; }
}

/// <summary>
/// 可安全重建交互卡片和 Tool 调用的最小快照。questions payload 不包含模型提供的 answers。
/// </summary>
internal sealed record DurableHumanInteractionSnapshot
{
    /// <summary>
    /// 获取人工请求的稳定标识。
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// 获取交互种类，例如 interaction 或 tool-approval。
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 获取产生请求的 Agentflow 节点标识。
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// 获取产生请求的可选节点名称。
    /// </summary>
    public string? NodeName { get; init; }

    /// <summary>
    /// 获取等待恢复的 Tool 名称。
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// 获取等待恢复的 Tool 调用标识。
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// 获取展示给用户的提示文本。
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// 获取客户端重建交互界面所需的安全载荷。
    /// </summary>
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// 获取恢复 Tool 调用所需的原始参数副本。
    /// </summary>
    public JsonElement? Arguments { get; init; }
}

/// <summary>
/// 将 pending 请求与对应人工回答绑定，供下一 durable segment 恢复。
/// </summary>
/// <param name="Request">上一分段持久化的人工请求。</param>
/// <param name="Response">PostgreSQL 中持久化的人工回答。</param>
internal sealed record DurableResolvedInteraction(
    DurableHumanInteractionSnapshot Request,
    DurableHumanResponseEnvelope Response
);

/// <summary>
/// 协调层返回给 connection attachment 的最小执行状态。
/// </summary>
/// <param name="ExecutionId">业务执行标识。</param>
/// <param name="Status">当前执行状态。</param>
/// <param name="StreamingScopeId">原始用户消息标识，用于把恢复消息绑定到同一轮历史。</param>
internal sealed record DurableExecutionStatusResponse(
    Guid ExecutionId,
    DurableExecutionStatus Status,
    string StreamingScopeId
);

/// <summary>
/// 从 SignalR HumanResponseCommand 映射得到的 durable 回答请求。
/// </summary>
/// <param name="ExecutionId">回答所属的业务执行标识。</param>
/// <param name="RequestId">回答对应的人工请求标识。</param>
/// <param name="Approved">用户是否批准请求。</param>
/// <param name="ResponseText">用户提交的可选文本回答。</param>
/// <param name="ApprovalScope">Tool approval 的生效范围。</param>
/// <param name="ResponseData">用户提交的可选结构化回答。</param>
internal sealed record SubmitDurableHumanResponseRequest(
    Guid ExecutionId,
    string RequestId,
    bool Approved,
    string? ResponseText = null,
    string ApprovalScope = "once",
    JsonElement? ResponseData = null
);
