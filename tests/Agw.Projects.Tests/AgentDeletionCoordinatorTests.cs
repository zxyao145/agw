using System.Data.Common;
using Agw.Agents.Definitions.Facades;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agw.Projects.Tests;

public sealed partial class ProjectDeletionCoordinatorTests
{
    [Theory]
    [InlineData("workflow")]
    [InlineData("enabled-job")]
    [InlineData("disabled-job")]
    public async Task DeleteAgentAsync_DefinitionReference_RejectsWithoutDeletingState(string reference)
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, conversation) = await SeedAgentStateAsync(context);
        if (reference == "workflow")
        {
            var flow = new Agentflow
            {
                Id = Guid.NewGuid(),
                CreateBy = "tester",
                Name = "dependent",
            };
            context.Agentflows.Add(flow);
            context.AgentflowNodes.Add(
                new AgentflowNode
                {
                    AgentflowId = flow.Id,
                    NodeId = "agent",
                    Kind = AgentflowNodeKind.Agent,
                    RelateId = agent.Id,
                }
            );
        }
        else
        {
            context.Jobs.Add(
                new Job
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    CreateBy = "tester",
                    Name = "dependent",
                    AgentId = agent.Id,
                    AgentType = AgentRuntimeType.Agent,
                    IsEnabled = reference == "enabled-job",
                }
            );
        }
        await context.SaveChangesAsync(token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            new AgentDeletionCoordinator(context, InMemoryApplicationLock.Shared).DeleteAsync(agent.Id, "tester", token)
        );

        Assert.Equal(ErrorCodes.AgentInUse.Code, error.Code);
        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
        Assert.Equal(1, await context.AgentSessionStates.CountAsync(token));
        Assert.Equal(1, await context.TaskSessionBindings.CountAsync(token));
    }

    [Fact]
    public async Task DeleteAgentAsync_Unreferenced_CleansStateAndRejectsLateCallbacks()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, project, conversation) = await SeedAgentStateAsync(context);
        var deletion = new AgentDeletionCoordinator(context, InMemoryApplicationLock.Shared);
        Assert.False(await deletion.DeleteAsync(agent.Id, "foreign", token));
        Assert.False(await deletion.DeleteAsync(Guid.NewGuid(), "foreign", token));

        Assert.True(await deletion.DeleteAsync(agent.Id, "tester", token));

        using (UserInfoUtil.PushSystemScope())
        {
            Assert.Empty(await context.AgentSessionStates.ToListAsync(token));
            Assert.Empty(await context.TaskSessionBindings.ToListAsync(token));
        }
        context.ChangeTracker.Clear();
        Assert.False(
            await new AgentSessionStatePersistence(context).SaveAsync(
                project.Id,
                conversation.Id,
                agent.Id,
                "",
                "late-state",
                "tester",
                TimeProvider.System.GetUtcNow(),
                token
            )
        );
        var bindings = new TaskSessionBindingService(
            context,
            TimeProvider.System,
            new TestUserInfoService(),
            new AgentCatalogFacade(context, new TestUserInfoService())
        );
        await Assert.ThrowsAsync<AgwException>(() =>
            bindings.UpsertAsync(project.Id, conversation.ContextId, agent.Id, "codex", "late-binding", "tester", token)
        );
        Assert.Equal(1, await context.ProjectConversations.CountAsync(token));
    }

    [Fact]
    public async Task DeleteAgentAsync_FailureAfterStateCleanup_RollsBackAllDeletes()
    {
        using var user = PushUser("tester");
        var token = TestContext.Current.CancellationToken;
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options);
        await using var context = new AgwDbContext(options);
        var (agent, _, _) = await SeedAgentStateAsync(context);
        var failingOptions = new DbContextOptionsBuilder<AgwDbContext>(options)
            .AddInterceptors(new FailAgentDelete())
            .Options;
        await using var failingContext = new AgwDbContext(failingOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentDeletionCoordinator(failingContext, InMemoryApplicationLock.Shared).DeleteAsync(
                agent.Id,
                "tester",
                token
            )
        );

        Assert.True(await context.Agents.AnyAsync(item => item.Id == agent.Id, token));
        Assert.Equal(1, await context.AgentSessionStates.CountAsync(token));
        Assert.Equal(1, await context.TaskSessionBindings.CountAsync(token));
    }

    private static async Task<(Agent Agent, Project Project, ProjectConversation Conversation)> SeedAgentStateAsync(
        AgwDbContext context
    )
    {
        var agent = new Agent
        {
            Id = Guid.NewGuid(),
            Name = "agent",
            CreateBy = "tester",
        };
        var project = new Project { Id = Guid.NewGuid(), CreateBy = "tester" };
        var conversation = new ProjectConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            ContextId = "agent-state",
            CreateBy = "tester",
        };
        context.AddRange(agent, project, conversation);
        context.AgentSessionStates.Add(
            new AgentSessionStateEntry
            {
                AgentId = agent.Id,
                ProjectConversationId = conversation.Id,
                SerializedSession = "{}",
            }
        );
        context.TaskSessionBindings.Add(
            new TaskSessionBinding
            {
                Id = Guid.NewGuid(),
                AgentId = agent.Id,
                ProjectConversationId = conversation.Id,
                ExternalAgentName = "codex",
                ProviderSessionId = "session",
                CreateBy = "tester",
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (agent, project, conversation);
    }

    private sealed class FailAgentDelete : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.StartsWith("DELETE FROM \"agent\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected deletion failure.");
            }
            return ValueTask.FromResult(result);
        }
    }
}
