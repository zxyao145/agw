using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class TurnAcceptanceTests
{
    [Fact]
    public async Task ReadAgentflowNodeStatusesAsync_TwoActiveAgentflowTurns_ReturnsRequestedTurnStatuses()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var agentflowId = Guid.CreateVersion7();
        await _kit.SeedAgentflowAsync(agentflowId);
        var accepted = await AcceptAgentflowAsync(task, agentflowId, durable: _kit.Coordinator);
        var secondTurn = await AcceptAgentflowAsync(task, agentflowId, durable: _kit.Coordinator);
        await AppendMixedEventsAsync(accepted.Request.TurnId);

        var snapshot = await _kit.Coordinator.ReadAgentflowNodeStatusesAsync(
            task.ProjectId,
            task.ProjectConversationId,
            accepted.Request.TurnId,
            token
        );

        Assert.NotNull(snapshot);
        Assert.Equal(accepted.Request.TurnId, snapshot.TurnId);
        Assert.Equal(agentflowId, snapshot.AgentflowId);
        Assert.Equal(1, snapshot.AgentflowRevision);
        Assert.Equal(
            [
                new AgentflowNodeStatusEntry("input", AgwAgentflowNodeStatus.Running, null),
                new("input", AgwAgentflowNodeStatus.Completed, null),
            ],
            snapshot.Statuses
        );
        var secondSnapshot = await _kit.Coordinator.ReadAgentflowNodeStatusesAsync(
            task.ProjectId,
            task.ProjectConversationId,
            secondTurn.Request.TurnId,
            token
        );
        Assert.NotNull(secondSnapshot);
        Assert.Equal(secondTurn.Request.TurnId, secondSnapshot.TurnId);
        Assert.Empty(secondSnapshot.Statuses);
    }

    [Fact]
    public async Task ReadAgentflowNodeStatusesAsync_OtherUser_ReturnsNull()
    {
        var task = await _kit.SeedConversationAsync();
        var agentflowId = Guid.CreateVersion7();
        await _kit.SeedAgentflowAsync(agentflowId);
        var accepted = await AcceptAgentflowAsync(task, agentflowId, durable: _kit.Coordinator);

        using var otherUser = TurnPersistenceTestKit.EnterUser("another-user");
        var snapshot = await _kit.Coordinator.ReadAgentflowNodeStatusesAsync(
            task.ProjectId,
            task.ProjectConversationId,
            accepted.Request.TurnId,
            TestContext.Current.CancellationToken
        );

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task ReadAgentflowNodeStatusesAsync_TurnFromAnotherConversation_ReturnsNull()
    {
        var task = await _kit.SeedConversationAsync();
        var otherTask = await _kit.SeedConversationAsync();
        var agentflowId = Guid.CreateVersion7();
        await _kit.SeedAgentflowAsync(agentflowId);
        var accepted = await AcceptAgentflowAsync(task, agentflowId, durable: _kit.Coordinator);

        var snapshot = await _kit.Coordinator.ReadAgentflowNodeStatusesAsync(
            otherTask.ProjectId,
            otherTask.ProjectConversationId,
            accepted.Request.TurnId,
            TestContext.Current.CancellationToken
        );

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task ReadAgentflowNodeStatusesAsync_AgentTurn_ReturnsNull()
    {
        var task = await _kit.SeedConversationAsync();
        var accepted = await AcceptAsync(task, durable: _kit.Coordinator);

        var snapshot = await _kit.Coordinator.ReadAgentflowNodeStatusesAsync(
            task.ProjectId,
            task.ProjectConversationId,
            accepted.Request.TurnId,
            TestContext.Current.CancellationToken
        );

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task ReadAgentflowNodeStatusesAsync_FinishedExecution_ReturnsNull()
    {
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var agentflowId = Guid.CreateVersion7();
        await _kit.SeedAgentflowAsync(agentflowId);
        var accepted = await AcceptAgentflowAsync(task, agentflowId, durable: _kit.Coordinator);
        await using (var context = _kit.CreateContext())
        {
            var execution = await context.DurableExecutions.SingleAsync(
                item => item.Id == accepted.Request.TurnId,
                token
            );
            execution.Status = DurableExecutionStatus.Completed;
            await context.SaveChangesAsync(token);
        }

        var snapshot = await _kit.Coordinator.ReadAgentflowNodeStatusesAsync(
            task.ProjectId,
            task.ProjectConversationId,
            accepted.Request.TurnId,
            token
        );

        Assert.Null(snapshot);
    }
}
