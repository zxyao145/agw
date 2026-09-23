using Agw.Agents.Execution.Inbound.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Outbound.SignalR;

internal sealed class SignalRExecutionMessageSink : IExecutionMessageSink
{
    private readonly string _connectionId;
    private readonly IHubContext<ExecutionHub, IExecutionHubClient> _hubContext;
    private readonly Func<bool> _isAttached;
    private readonly ILogger _logger;

    public SignalRExecutionMessageSink(
        string connectionId,
        IHubContext<ExecutionHub, IExecutionHubClient> hubContext,
        Func<bool> isAttached,
        ILogger logger
    )
    {
        _connectionId = connectionId;
        _hubContext = hubContext;
        _isAttached = isAttached;
        _logger = logger;
    }

    public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
    {
        if (!_isAttached())
        {
            return;
        }

        try
        {
            await _hubContext.Clients.Client(_connectionId).ReceiveMessage(message);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to send execution message to {ConnectionId}.", _connectionId);
        }
    }
}
