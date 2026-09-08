namespace Agw.Agents.Execution.Outbound.SignalR;

public interface IExecutionHubClient
{
    Task ReceiveMessage(AgwMessage message);
}
