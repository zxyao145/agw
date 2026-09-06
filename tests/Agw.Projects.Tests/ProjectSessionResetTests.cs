using Agw.Agents.Definitions.Facades;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Tests;

public sealed partial class ProjectDeletionCoordinatorTests
{
    [Theory]
    [InlineData(int.MaxValue - 1)]
    [InlineData(int.MaxValue)]
    public async Task ClearConversationRecordsAsync_GenerationLimit_NeverOverflows(int generation)
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await using var context = new AgwDbContext(options);
        await context.ProjectConversations.ExecuteUpdateAsync(
            setters => setters.SetProperty(conversation => conversation.Generation, generation),
            token
        );
        var coordinator = TestProjectPersistence.CreateDeletionCoordinator(context);
        var target = new ProjectConversationDeletionTarget(projectId, conversationId, "context-1", "tester");

        if (generation == int.MaxValue)
        {
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                coordinator.ClearConversationRecordsAsync(target, token)
            );
            Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
            Assert.Equal(1, await context.ProjectConversationChatHistories.CountAsync(token));
        }
        else
        {
            Assert.True(await coordinator.ClearConversationRecordsAsync(target, token));
            Assert.Empty(await context.ProjectConversationChatHistories.ToListAsync(token));
        }
        Assert.Equal(int.MaxValue, (await context.ProjectConversations.AsNoTracking().SingleAsync(token)).Generation);
        Assert.Equal(1, await context.AgentUsages.CountAsync(token));
    }

    [Fact]
    public async Task ClearConversationRecordsAsync_ResetsSdkStateAndRejectsLateWrites()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await using var context = new AgwDbContext(options);
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            CreateBy = "tester",
            Name = "reset-agent",
        };
        context.Agents.Add(agent);
        await context.SaveChangesAsync(token);
        var sessions = new AgentSessionStatePersistence(context);
        Assert.True(
            await sessions.SaveAsync(
                projectId,
                conversationId,
                agent.Id,
                "",
                "old-state",
                "tester",
                TimeProvider.System.GetUtcNow(),
                token
            )
        );

        Assert.True(
            await TestProjectPersistence
                .CreateDeletionCoordinator(context)
                .ClearConversationRecordsAsync(
                    new ProjectConversationDeletionTarget(projectId, conversationId, "context-1", "tester"),
                    token
                )
        );

        var conversation = await context.ProjectConversations.AsNoTracking().SingleAsync(token);
        Assert.Equal(1, conversation.Generation);
        Assert.Equal("context-1", conversation.ContextId);
        Assert.Equal("context-1", conversation.Title);
        Assert.Equal(1, await context.AgentUsages.CountAsync(token));
        Assert.Empty(await context.ProjectConversationChatHistories.ToListAsync(token));
        Assert.Empty(await context.TaskSessionBindings.ToListAsync(token));
        Assert.Empty(await context.AgentflowCheckpoints.ToListAsync(token));
        using (UserInfoUtil.PushSystemScope())
        {
            Assert.Empty(await context.AgentSessionStates.ToListAsync(token)); // Includes legacy rows with a missing Agent.
        }

        var trackedSessionError = await Assert.ThrowsAsync<AgwException>(() =>
            sessions.SaveAsync(
                projectId,
                conversationId,
                agent.Id,
                "",
                "late-tracked-state",
                "tester",
                TimeProvider.System.GetUtcNow(),
                token
            )
        );
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, trackedSessionError.Code);

        await using var lateContext = new AgwDbContext(options);
        var lateSessions = new AgentSessionStatePersistence(lateContext);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            lateSessions.SaveAsync(
                projectId,
                conversationId,
                agent.Id,
                "",
                "late-old-state",
                "tester",
                TimeProvider.System.GetUtcNow(),
                token
            )
        );
        Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        lateContext.ChangeTracker.Clear();
        Assert.True(
            await lateSessions.SaveAsync(
                projectId,
                conversationId,
                agent.Id,
                "",
                "new-state",
                "tester",
                TimeProvider.System.GetUtcNow(),
                token,
                1
            )
        );
        Assert.Null(await lateSessions.ReadAsync(projectId, conversationId, agent.Id, "", "tester", token));
        Assert.Equal(
            "new-state",
            await lateSessions.ReadAsync(projectId, conversationId, agent.Id, "", "tester", token, 1)
        );

        var bindings = new TaskSessionBindingService(
            lateContext,
            TimeProvider.System,
            new TestUserInfoService(),
            new AgentCatalogFacade(lateContext, new TestUserInfoService())
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            bindings.UpsertAsync(projectId, "context-1", agent.Id, "codex", "late-session", "tester", token)
        );
        Assert.Empty(await lateContext.TaskSessionBindings.ToListAsync(token));
    }

    [Fact]
    public async Task ClearConversationRecordsAsync_ActiveInProcessTurn_PreservesAllState()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await using var context = new AgwDbContext(options);
        var gate = new ConversationExecutionGate(context, InMemoryApplicationLock.Shared, TimeProvider.System);
        await using (await gate.AcquireAsync(conversationId, 0, token))
        {
            var error = await Assert.ThrowsAsync<AgwException>(() =>
                TestProjectPersistence
                    .CreateDeletionCoordinator(context)
                    .ClearConversationRecordsAsync(
                        new ProjectConversationDeletionTarget(projectId, conversationId, "context-1", "tester"),
                        token
                    )
            );
            Assert.Equal(ErrorCodes.ConversationSessionConflict.Code, error.Code);
        }
        Assert.Equal(0, (await context.ProjectConversations.AsNoTracking().SingleAsync(token)).Generation);
        Assert.Equal(1, await context.ProjectConversationChatHistories.CountAsync(token));
        Assert.True(
            await TestProjectPersistence
                .CreateDeletionCoordinator(context)
                .ClearConversationRecordsAsync(
                    new ProjectConversationDeletionTarget(projectId, conversationId, "context-1", "tester"),
                    token
                )
        );
        await Assert.ThrowsAsync<AgwException>(() => gate.AcquireAsync(conversationId, 0, token));
        await using var newTurn = await gate.AcquireAsync(conversationId, 1, token);
    }

    [Fact]
    public async Task ClearConversationRecordsAsync_QueuedDurableExecution_ReturnsConflict()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        var projectId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await SeedProjectGraphAsync(options, projectId, conversationId, "tester", "context-1");
        await SeedDurableExecutionAsync(options, projectId, conversationId, "tester");
        await using var context = new AgwDbContext(options);

        await Assert.ThrowsAsync<AgwException>(() =>
            TestProjectPersistence
                .CreateDeletionCoordinator(context)
                .ClearConversationRecordsAsync(
                    new ProjectConversationDeletionTarget(projectId, conversationId, "context-1", "tester"),
                    token
                )
        );

        Assert.Equal(0, (await context.ProjectConversations.AsNoTracking().SingleAsync(token)).Generation);
        Assert.Equal(1, await context.ProjectConversationChatHistories.CountAsync(token));
    }
}
