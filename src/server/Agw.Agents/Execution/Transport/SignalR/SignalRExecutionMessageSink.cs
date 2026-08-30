using Agw.Agents.Execution.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Transport.SignalR;

internal sealed class SignalRExecutionMessageSink(
    string connectionId,
    IHubContext<ExecutionHub, IExecutionHubClient> hubContext,
    Func<bool> isAttached,
    ILogger logger
) : IExecutionMessageSink
{
    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        if (!isAttached())
        {
            return;
        }

        try
        {
            await hubContext.Clients.Client(connectionId).ReceiveMessage(message);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to send execution message to {ConnectionId}.", connectionId);
        }
    }
}
