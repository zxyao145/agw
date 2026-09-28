using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared;
using Agw.Shared.Data.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class DurableExecutionAuditTests
{
    [Fact]
    public async Task StateTransition_UsesPersistedExecutionOwnerForAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var auditProvider = new CurrentUserAuditProvider();
        await using var kit = await TurnPersistenceTestKit.CreateAsync(
            interceptors:
            [
                new EntityCreatorInterceptor(auditProvider, TimeProvider.System),
                new EntityModifierInterceptor(auditProvider, TimeProvider.System),
                new EntitySoftDeleteInterceptor(auditProvider, TimeProvider.System),
            ]
        );
        Guid turnId;
        using (TurnPersistenceTestKit.EnterUser("owner"))
        {
            var task = await kit.SeedConversationAsync("owner");
            var accepted = await kit.CreateAcceptance(
                    projectTasks: null,
                    new AgentExecutionFacadeTests.WorkspaceProjects(),
                    kit.Coordinator
                )
                .AcceptAsync(
                    new TurnAcceptanceRequest(
                        "owner",
                        TurnId: null,
                        new ExecutionTarget(Guid.CreateVersion7(), AgentRuntimeType.Agent),
                        task.ProjectConversationId,
                        TurnPersistenceTestKit.CreateInput("run"),
                        TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                        Stream: true
                    )
                    {
                        Task = task,
                    },
                    cancellationToken
                );
            turnId = accepted.Request.TurnId;
        }

        // Act
        using (UserInfoUtil.PushSystemScope())
        {
            var lease = await kit.Leases.TryClaimAsync(turnId, "worker-a", TimeSpan.FromSeconds(30), cancellationToken);
            using var ownership = new CancellationTokenSource();
            await kit
                .Leases.CreateGuard(Assert.IsType<DurableLease>(lease), ownership)
                .RunAsync(
                    (services, token) =>
                        services
                            .GetRequiredService<DurableExecutionStore>()
                            .ApplySegmentResultAsync(
                                new DurableExecutionSegmentResult
                                {
                                    TurnId = turnId,
                                    SegmentIndex = 0,
                                    Status = DurableExecutionSegmentStatus.WaitingForHuman,
                                    PendingInteractions = [InteractionTestData.Input("request-1")],
                                },
                                token
                            ),
                    cancellationToken
                );
        }

        // Assert
        var persisted = await kit.ReadExecutionAsync(turnId);
        Assert.Equal("owner", persisted.CreateBy);
        Assert.Equal("owner", persisted.UpdateBy);
    }

    private sealed class CurrentUserAuditProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.UserId ?? Constants.AdminUserId;
    }
}
