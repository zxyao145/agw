using Agw.Agents.Execution.Inbound.Facades;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task FacadeExecuteAsync_Distributed_AcceptsTurnAndReturnsCommittedOutcome()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var facade = new AgentExecutionFacade(
            _kit.CreateAcceptance(
                projectTasks: null,
                new AgentExecutionFacadeTests.WorkspaceProjects(),
                _kit.Coordinator
            ),
            new AgentExecutionFacadeTests.AlwaysOwnedAgentCatalog(),
            _kit.ResolveScoped<Agw.Projects.Contracts.History.IConversationTurnStore>(),
            durableCoordinator: _kit.Coordinator
        );
        var task = await _kit.SeedConversationAsync();
        var turnId = Guid.CreateVersion7();
        var request = new AgentExecutionRequest(
            turnId,
            UserId,
            new AgentTarget(AgentTargetKind.Agent, Guid.CreateVersion7()),
            new ProjectTaskSnapshot(
                task.TaskId,
                task.ProjectConversationId,
                task.ProjectId,
                task.ContextId,
                null,
                task.Title,
                ProjectTaskStatus.Running,
                null,
                task.CreateTime,
                null,
                null
            ),
            TurnPersistenceTestKit.CreateInput("run"),
            HumanInteractionPolicy: HumanInteractionPolicy.Reject,
            PermissionMode: AgentExecutionPermissionMode.FullAccess
        );

        // Act: the Facade accepts the turn and reads committed events; another instance claims and completes it.
        var execution = facade.ExecuteAsync(request, token);
        while (!await ExecutionExistsAsync(turnId))
        {
            Assert.False(execution.IsCompleted, "The Facade stopped before accepting the turn.");
            await Task.Delay(20, token);
        }
        var lease = await ClaimAsync(turnId, "worker-b");
        var segment = Assert.IsType<DurableExecutionSnapshot>(await Store.LoadClaimedAsync(turnId, token));
        using var ownership = new CancellationTokenSource();
        await _kit.Coordinator.CommitResultAsync(
            segment,
            lease,
            _kit.Leases.CreateGuard(lease, ownership),
            new DurableExecutionSegmentResult
            {
                TurnId = turnId,
                SegmentIndex = segment.SegmentIndex,
                Status = DurableExecutionSegmentStatus.Completed,
                StepCount = 1,
            },
            failureReported: true,
            token
        );
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(10), token);

        // Assert
        Assert.Equal(AgentExecutionState.Completed, result.State);
        Assert.DoesNotContain(result.Messages, AgwMessageClassifier.IsTurnFinished);
        var snapshot = await Store.GetAsync(turnId, token);
        Assert.Equal(DurableExecutionStatus.Completed, snapshot.Status);
        Assert.Equal(AgwPermissionMode.FullAccess, snapshot.Manifest.Settings.PermissionMode);
        Assert.Equal(HumanInteractionPolicy.Reject, snapshot.Manifest.Settings.HumanInteractionPolicy);
        Assert.NotNull(snapshot.Manifest.WorkspaceSnapshot);
        var turn = await _kit.ReadTurnAsync(turnId);
        Assert.Equal(ProjectConversationTurnStatus.Completed, turn.Status);
        Assert.Equal(1, turn.StepCount);
        Assert.Equal([1L, 2L], (await _kit.ReadEventsAsync(turnId)).Select(item => item.TurnSequence));
    }

    private async Task<bool> ExecutionExistsAsync(Guid turnId)
    {
        await using var context = _kit.CreateContext();
        return await context
            .DurableExecutions.IgnoreQueryFilters()
            .AnyAsync(item => item.Id == turnId, TestContext.Current.CancellationToken);
    }
}
