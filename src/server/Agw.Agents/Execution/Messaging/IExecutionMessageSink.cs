namespace Agw.Agents.Execution.Messaging;

public interface IExecutionMessageSink
{
    ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken);
}
