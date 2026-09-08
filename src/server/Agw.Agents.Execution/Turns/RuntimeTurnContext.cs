using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;

namespace Agw.Agents.Execution.Turns;

public sealed record RuntimeTurnContext
{
    public RuntimeTurnContext(
        ExecutionSettings settings,
        AgentExecutionTask task,
        ExecutionTarget target,
        string workspace,
        IExecutionMessageSink messageSink,
        Action<HumanGateApprovalRequest?>? pendingHumanGateChanged = null
    )
    {
        Settings = settings;
        Task = task;
        Target = target;
        Workspace = workspace;
        MessageSink = messageSink;
        PendingHumanGateChanged = pendingHumanGateChanged;
    }

    public ExecutionSettings Settings { get; }

    public AgentExecutionTask Task { get; }

    public ExecutionTarget Target { get; }

    public Guid ProjectId => Task.ProjectId;

    public Guid ProjectConversationId => Task.ProjectConversationId;

    public string ContextId => Task.ContextId;

    public Guid AgentId => Target.AgentId;

    public AgentRuntimeType AgentType => Target.AgentType;

    public string UserId { get; init; } = Constants.AdminUserId;

    public string Workspace { get; }

    public IExecutionMessageSink MessageSink { get; }

    public Action<HumanGateApprovalRequest?>? PendingHumanGateChanged { get; }
}
