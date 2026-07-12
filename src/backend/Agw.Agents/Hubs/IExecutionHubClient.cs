using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Hubs;

public interface IExecutionHubClient
{
    Task ReceiveMessage(AgwMessage message);
}
