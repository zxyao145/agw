using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Execution.Transport.SignalR;

public interface IExecutionHubClient
{
    Task ReceiveMessage(AgwMessage message);
}
