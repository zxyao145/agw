using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Execution.Connections;

public interface IExecutionMessageSink
{
    ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken);
}
