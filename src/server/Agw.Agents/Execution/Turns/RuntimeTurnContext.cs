using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Messaging;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;

namespace Agw.Agents.Execution.Turns;

public sealed record RuntimeTurnContext
{
    public RuntimeTurnContext(
        ExecutionSettings settings,
        TaskProjection task,
        ExecutionTarget target,
        string userName,
        string workspace,
        IExecutionMessageSink messageSink,
        Action<HumanGateApprovalRequest?>? pendingHumanGateChanged = null
    )
    {
        Settings = settings;
        Task = task;
        Target = target;
        UserName = userName;
        Workspace = workspace;
        MessageSink = messageSink;
        PendingHumanGateChanged = pendingHumanGateChanged;
    }

    public ExecutionSettings Settings { get; }

    public TaskProjection Task { get; }

    public ExecutionTarget Target { get; }

    public Guid ProjectId => Task.ProjectId;

    public Guid ProjectConversationId => Task.ProjectConversationId;

    public string ContextId => Task.ContextId;

    public Guid AgentId => Target.AgentId;

    public AgentRuntimeType AgentType => Target.AgentType;

    public string UserName { get; }

    public string Workspace { get; }

    public IExecutionMessageSink MessageSink { get; }

    public Action<HumanGateApprovalRequest?>? PendingHumanGateChanged { get; }
}
