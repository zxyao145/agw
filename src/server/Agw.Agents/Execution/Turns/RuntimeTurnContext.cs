using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;

namespace Agw.Agents.Execution.Turns;

public sealed record RuntimeTurnContext
{
    public RuntimeTurnContext(
        SettingCommand settings,
        string userName,
        string workspace,
        IExecutionMessageSink messageSink,
        Action<HumanGateApprovalRequest?>? pendingHumanGateChanged = null)
    {
        Settings = settings;
        UserName = userName;
        Workspace = workspace;
        MessageSink = messageSink;
        PendingHumanGateChanged = pendingHumanGateChanged;
    }

    public SettingCommand Settings { get; }

    public string UserName { get; }

    public string Workspace { get; }

    public IExecutionMessageSink MessageSink { get; }

    public Action<HumanGateApprovalRequest?>? PendingHumanGateChanged { get; }
}
