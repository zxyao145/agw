using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Runtimes;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Runtimes.InProcess;

namespace Agw.Agents.Tests;

public partial class ExecutionCommandHandlerTests
{
    [Theory]
    [InlineData(AgentRuntimeType.Agent)]
    [InlineData(AgentRuntimeType.Agentflow)]
    public async Task StartAsync_InProcess_AcceptsBeforeExecutionCompletes(AgentRuntimeType agentType)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var factory = new FakeRuntimeFactory { HoldTurnOpen = true };
        var owner = new InProcessExecutionStarter(factory, "user-id", new CapturingSink(), token, _ => { });
        IExecutionStarter starter = owner;
        var task = CreateTask("starter");
        var request = new ExecutionStartRequest(
            Guid.CreateVersion7(),
            new ExecutionTarget(Guid.CreateVersion7(), agentType),
            task,
            ExecutionSettings.CreateDefault(),
            CreateExecCommand(Guid.CreateVersion7()).Input,
            true,
            "/workspace"
        );

        try
        {
            // Act
            var receipt = await starter.StartAsync(request, token).WaitAsync(TimeSpan.FromSeconds(5), token);

            // Assert
            Assert.True(receipt.Accepted);
            Assert.Equal(request.ExecutionId, receipt.ExecutionId);
            Assert.True(owner.Runtime!.HasActiveTurn);
            var context = Assert.Single(factory.StartRequests).TurnContext;
            Assert.Equal(agentType, context.AgentType);
            Assert.Equal(request.Target.AgentId, context.AgentId);
            Assert.Equal("user-id", context.UserId);
            Assert.Equal(request.Workspace, context.Workspace);
        }
        finally
        {
            factory.CompleteHeldTurn();
            await owner.ReleaseRuntimeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_InProcessCanceledBeforeAcceptance_DoesNotCreateRuntime()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var factory = new FakeRuntimeFactory();
        var owner = new InProcessExecutionStarter(
            factory,
            "user-id",
            new CapturingSink(),
            CancellationToken.None,
            _ => { }
        );
        IExecutionStarter starter = owner;
        var request = new ExecutionStartRequest(
            Guid.CreateVersion7(),
            new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
            CreateTask("starter"),
            ExecutionSettings.CreateDefault(),
            CreateExecCommand(Guid.CreateVersion7()).Input,
            true,
            "/workspace"
        );

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.StartAsync(request, cancellation.Token));

        // Assert
        Assert.Null(owner.Runtime);
        Assert.Empty(factory.CreatedRuntimes);
    }

    [Fact]
    public async Task StartTurnAsync_UnavailableRuntime_ReportsRejectionWithoutTarget()
    {
        // Arrange
        var sink = new CapturingSink();
        await using var context = CreateContext(new RejectingRuntimeFactory(), CreateTask("starter"), sink: sink);

        // Act
        await context.StartTurnAsync(CreateExecCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(context.HasActiveTurn);
        Assert.Null(context.Target);
        var error = Assert.IsType<AgwErrorContent>(
            Assert.Single(
                Assert
                    .Single(sink.Messages, message => message.Contents.Any(content => content is AgwErrorContent))
                    .Contents
            )
        );
        Assert.Equal("Agent execution could not be started.", error.Content);
    }

    private sealed class RejectingRuntimeFactory : IRuntimeFactory
    {
        public Task<RuntimeStartResult> StartAsync(RuntimeStartRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(default(RuntimeStartResult));
    }
}
