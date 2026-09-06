using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Agents;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Fact]
    public async Task ResetGeneration_LateHistoryAndCheckpoint_CannotRestoreOldState()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var task = CreateTask(database);
        var flowId = Guid.NewGuid();
        database.Context.Agentflows.Add(
            new Agentflow
            {
                Id = flowId,
                Name = "flow",
                CreateBy = "user-id",
            }
        );
        await database.Context.SaveChangesAsync(token);
        var services = new ServiceCollection();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped<IProjectsDbContext>(provider =>
            provider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>()
        );
        services.AddScoped<IAgentflowCheckpointPersistence>(provider => new AgentflowCheckpointPersistence(
            provider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>(),
            TestDurablePersistence.Create(provider.GetRequiredService<Agw.Infrastructure.Data.AgwDbContext>())
        ));
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var history = new EfCoreChatHistoryProvider(
            scopes,
            NullLogger<EfCoreChatHistoryProvider>.Instance,
            TimeProvider.System
        );
        var checkpoints = new AgentflowCheckpointStore(scopes, InMemoryApplicationLock.Shared, TimeProvider.System);
        using var oldExecution = ConversationSessionContext.Push(task.ProjectId, task.ContextId, 0);
        await database
            .Context.ProjectConversations.Where(conversation => conversation.Id == task.ProjectConversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(conversation => conversation.Generation, 1), token);

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
                "user-id",
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
        Assert.Empty(await database.Context.ProjectConversationChatHistories.ToListAsync(token));
        Assert.Empty(await database.Context.AgentflowCheckpoints.ToListAsync(token));

        using var newExecution = ConversationSessionContext.Push(task.ProjectId, task.ContextId, 1);
        await history.AppendAsync(
            task.ProjectId,
            task.ContextId,
            [new ChatMessage(ChatRole.User, "new-history")],
            token
        );
        Assert.Equal(1, await database.Context.ProjectConversationChatHistories.CountAsync(token));
    }

    [Fact]
    public async Task ResetGeneration_OldDurableManifest_CannotRegisterReplayOrRespond()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync();
        var store = database.CreateStore();
        var executionId = await RegisterExecutionAsync(database, store);
        var snapshot = await store.GetAsync(executionId, token);
        Assert.DoesNotContain("generation", DurableExecutionJson.Serialize(snapshot.Manifest.Task));
        await database
            .Context.ProjectConversations.Where(conversation =>
                conversation.Id == snapshot.Manifest.Task.ProjectConversationId
            )
            .ExecuteUpdateAsync(setters => setters.SetProperty(conversation => conversation.Generation, 1), token);

        await Assert.ThrowsAsync<AgwException>(() => store.GetAuthorizedAsync(executionId, "user-id", token));
        await Assert.ThrowsAsync<AgwException>(() =>
            store.RegisterAsync(
                executionId,
                "user-id",
                snapshot.Manifest.AgentId,
                snapshot.Manifest.AgentType,
                snapshot.Manifest.Input,
                snapshot.Manifest.Task.ToProjection(),
                CreateSettings(snapshot.Manifest.Task.ProjectId, snapshot.Manifest.Task.ContextId),
                token
            )
        );
    }
}
