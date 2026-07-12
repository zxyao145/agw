using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Runtime.Hubs;

public interface IExecutionHubClient
{
    Task ReceiveMessage(AgwMessage message);
}
