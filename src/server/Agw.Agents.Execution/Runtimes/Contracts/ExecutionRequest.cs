using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Runtime;

namespace Agw.Agents.Execution.Runtimes.Contracts;

/// <summary>
/// 受理层交给协调层的已受理 Turn：身份、目标、项目任务、工作目录快照与设置都已由服务端解析和校验，输入行与 Turn 行已经提交。
/// An accepted turn handed from acceptance to coordination: identity, target, project task, workspace snapshot and settings are resolved and verified by the server, and the input and turn rows are committed.
/// </summary>
internal sealed record ExecutionRequest(
    Guid TurnId,
    string UserId,
    ExecutionTarget Target,
    AgentExecutionTask Task,
    ExecutionSettings Settings,
    AgwUserInput Input,
    bool Stream,
    ProjectWorkspaceSnapshot WorkspaceSnapshot
)
{
    public string? RequestedMode { get; init; }

    public AgentflowCheckpointSnapshot? ResumeCheckpoint { get; init; }

    /// <summary>
    /// 开始与结束消息的字段来源。
    /// The source of the start and finish message fields.
    /// </summary>
    public required TurnEnvelope Envelope { get; init; }

    /// <summary>
    /// 受理时写入的用户输入行；没有输入的 Turn 为空。
    /// The user input row written at acceptance; null for a turn without input.
    /// </summary>
    public Guid? InputMessageId { get; init; }

    /// <summary>
    /// Durable 受理实例在本地执行时持有的租约；Queued 与进程内为空。
    /// The lease held when the accepting instance runs a durable turn locally; null for Queued and in-process turns.
    /// </summary>
    public DurableLease? Lease { get; init; }
}
