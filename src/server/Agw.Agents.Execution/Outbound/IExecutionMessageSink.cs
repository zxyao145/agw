namespace Agw.Agents.Execution.Outbound;

public interface IExecutionMessageSink
{
    ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken);
}
