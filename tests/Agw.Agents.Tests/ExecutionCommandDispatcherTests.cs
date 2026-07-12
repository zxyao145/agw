using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Contracts;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class ExecutionCommandDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_RegisteredCommand_InvokesMatchingHandler()
    {
        var handler = new CapturingHandler(typeof(InterruptCommand));
        var dispatcher = new ExecutionCommandDispatcher([handler]);
        var command = new InterruptCommand { Reason = "stop" };

        await dispatcher.DispatchAsync(
            command,
            connection: null!,
            TestContext.Current.CancellationToken);

        Assert.Same(command, handler.Command);
    }

    [Fact]
    public async Task DispatchAsync_UnregisteredCommand_ThrowsAgwException()
    {
        var dispatcher = new ExecutionCommandDispatcher([]);

        await Assert.ThrowsAsync<AgwException>(() => dispatcher.DispatchAsync(
            new InterruptCommand(),
            connection: null!,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Constructor_DuplicateCommandHandlers_ThrowsAgwException()
    {
        var first = new CapturingHandler(typeof(InterruptCommand));
        var second = new CapturingHandler(typeof(InterruptCommand));

        Assert.Throws<AgwException>(() => new ExecutionCommandDispatcher([first, second]));
    }

    private sealed class CapturingHandler(Type commandType) : IExecutionCommandHandler
    {
        public Type CommandType { get; } = commandType;

        public AgentRunCommand? Command { get; private set; }

        public Task HandleAsync(
            AgentRunCommand command,
            ExecutionConnection connection,
            CancellationToken cancellationToken)
        {
            Command = command;
            return Task.CompletedTask;
        }
    }
}
