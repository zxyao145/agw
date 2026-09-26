using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Agentflows.Checkpoints.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Infrastructure.Agents;
using Agw.Projects.Contracts.History;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class ConversationGenerationPersistenceTests
{
    [Fact]
    public async Task ResetGeneration_LateHistoryAndCheckpoint_CannotRestoreOldState()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var user = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync(configure: services =>
            services.AddScoped<IAgentflowCheckpointPersistence, AgentflowCheckpointPersistence>()
        );
        var task = await kit.SeedConversationAsync();
        var flowId = Guid.CreateVersion7();
        await using (var context = kit.CreateContext())
        {
            context.Agentflows.Add(
                new Agentflow
                {
                    Id = flowId,
                    Name = "flow",
                    CreateBy = TurnPersistenceTestKit.UserId,
                }
            );
            await context.SaveChangesAsync(token);
        }
        var history = new Agw.Agents.Execution.Agents.History.ConversationHistoryWriter(
            kit.Services.GetRequiredService<IConversationHistoryStore>(),
            TimeProvider.System
        );
        var checkpoints = new AgentflowCheckpointStore(
            kit.Services.GetRequiredService<IServiceScopeFactory>(),
            kit.Locks,
            TimeProvider.System
        );
        await kit.SetGenerationAsync(task.ProjectConversationId, 1);

        // Act
        using (ExecutionTestScopes.PushIdentity(task.ProjectId, task.ContextId, 0))
        {
            await Assert.ThrowsAsync<AgwException>(() =>
                history.AppendAsync(
                    task.ProjectId,
                    task.ContextId,
                    [new ChatMessage(ChatRole.User, "late-old-history")],
                    token
                )
            );
            await Assert.ThrowsAsync<AgwException>(() =>
                checkpoints.RecordAsync(
                    null,
                    task.ProjectId,
                    task.ProjectConversationId,
                    task.ContextId,
                    task.TaskId,
                    flowId,
                    TurnPersistenceTestKit.UserId,
                    false,
                    new string('a', 64),
                    new DurableAgentflowCheckpoint
                    {
                        SessionId = "old",
                        CheckpointId = "old",
                        Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { step = 1 }),
                    },
                    new Dictionary<string, string> { ["marker"] = "Marker" },
                    token
                )
            );
        }
        using (ExecutionTestScopes.PushIdentity(task.ProjectId, task.ContextId, 1))
        {
            await history.AppendAsync(
                task.ProjectId,
                task.ContextId,
                [new ChatMessage(ChatRole.User, "new-history")],
                token
            );
        }

        // Assert
        var rows = await kit.ReadHistoryAsync(task.ProjectConversationId);
        Assert.Single(rows);
        await using var assertContext = kit.CreateContext();
        Assert.Empty(await assertContext.AgentflowCheckpoints.IgnoreQueryFilters().ToListAsync(token));
    }

    [Fact]
    public async Task ResetGeneration_OldDurableManifest_CannotAcceptReplayOrRespond()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var user = TurnPersistenceTestKit.EnterUser();
        await using var kit = await TurnPersistenceTestKit.CreateAsync();
        var task = await kit.SeedConversationAsync();
        var agentId = Guid.CreateVersion7();
        var request = new TurnAcceptanceRequest(
            TurnPersistenceTestKit.UserId,
            Guid.CreateVersion7(),
            new Agw.Agents.Execution.Inbound.Connections.ExecutionTarget(agentId, AgentRuntimeType.Agent),
            task.ProjectConversationId,
            TurnPersistenceTestKit.CreateInput("hello"),
            TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
            Stream: true
        )
        {
            Task = task,
        };
        var accepted = await kit.CreateAcceptance(
                projectTasks: null,
                new AgentExecutionFacadeTests.WorkspaceProjects(),
                kit.Coordinator
            )
            .AcceptAsync(request, token);
        var store = kit.ResolveScoped<DurableExecutionStore>();
        var snapshot = await store.GetAsync(accepted.Request.TurnId, token);
        Assert.DoesNotContain("generation", DurableExecutionJson.Serialize(snapshot.Manifest.Task));
        await kit.SetGenerationAsync(task.ProjectConversationId, 1);

        // Act
        var read = await Assert.ThrowsAsync<AgwException>(() =>
            store.GetAuthorizedAsync(accepted.Request.TurnId, TurnPersistenceTestKit.UserId, token)
        );
        var replay = await Assert.ThrowsAsync<AgwException>(() =>
            kit.CreateAcceptance(projectTasks: null, new AgentExecutionFacadeTests.WorkspaceProjects(), kit.Coordinator)
                .AcceptAsync(request with { TurnId = Guid.CreateVersion7() }, token)
        );

        // Assert
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, read.Code);
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, replay.Code);
    }
}
