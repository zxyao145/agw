using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class AgentflowRevisionModelTests
{
    [Fact]
    public async Task SaveChanges_SameNodeIdInTwoRevisions_KeepsBothRows()
    {
        using var database = new TestAgentDatabase();
        _ = new TestUserInfoService("tester");
        var agentflowId = Guid.CreateVersion7();
        database.Context.Agentflows.Add(
            new Agentflow
            {
                Id = agentflowId,
                Name = "revision-test",
                CreateBy = "tester",
            }
        );
        database.Context.AgentflowRevisions.AddRange(
            new AgentflowRevision
            {
                AgentflowId = agentflowId,
                Revision = 1,
                CreateBy = "tester",
            },
            new AgentflowRevision
            {
                AgentflowId = agentflowId,
                Revision = 2,
                CreateBy = "tester",
            }
        );
        database.Context.AgentflowNodes.AddRange(
            new AgentflowNode
            {
                AgentflowId = agentflowId,
                AgentflowRevision = 1,
                NodeId = "input",
            },
            new AgentflowNode
            {
                AgentflowId = agentflowId,
                AgentflowRevision = 2,
                NodeId = "input",
            }
        );

        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await database.Context.AgentflowNodes.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Query_BlockMemberOfAnotherUser_IsNotVisible()
    {
        using var database = new TestAgentDatabase();
        _ = new TestUserInfoService(string.Empty);
        var agentflowId = Guid.CreateVersion7();
        database.Context.Agentflows.Add(
            new Agentflow
            {
                Id = agentflowId,
                Name = "other-user-flow",
                CreateBy = "other-user",
            }
        );
        database.Context.AgentflowRevisions.Add(
            new AgentflowRevision
            {
                AgentflowId = agentflowId,
                Revision = 1,
                CreateBy = "other-user",
            }
        );
        database.Context.AgentflowBlockMembers.Add(
            new AgentflowBlockMember
            {
                AgentflowId = agentflowId,
                AgentflowRevision = 1,
                NodeId = "team",
                MemberId = "member-1",
                Kind = AgentflowNodeKind.Agent,
                RelateId = Guid.CreateVersion7(),
                Name = "Member",
                CreateBy = "other-user",
            }
        );
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        _ = new TestUserInfoService("tester");

        Assert.Empty(await database.Context.AgentflowRevisions.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await database.Context.AgentflowBlockMembers.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveChanges_ExtraNotAssigned_StoresEmptyObject()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var traceId = Guid.CreateVersion7();
        var turnId = Guid.CreateVersion7();
        var rawTraceId = Guid.CreateVersion7();
        var rawTurnId = Guid.CreateVersion7();
        context.AgentflowNodeExecutionTraces.Add(
            new AgentflowTrace
            {
                Id = traceId,
                StartTimeUtc = now,
                ProjectId = Guid.CreateVersion7(),
                ContextId = "trace-context",
                TaskId = Guid.CreateVersion7(),
                AgentflowId = Guid.CreateVersion7(),
                NodeId = "input",
                NodeKind = AgentflowNodeKind.Input,
                Input = "input",
                Status = AgentflowNodeExecutionStatus.Succeeded,
            }
        );
        context.ProjectConversationTurns.Add(
            new ProjectConversationTurn
            {
                Id = turnId,
                ProjectConversationId = Guid.CreateVersion7(),
                TargetId = Guid.CreateVersion7(),
                RuntimeType = AgentRuntimeType.Agentflow,
                Status = ProjectConversationTurnStatus.Accepted,
                InputMessageId = Guid.CreateVersion7(),
                FirstSequence = 1,
                StepCount = 0,
                StartedAt = now,
            }
        );
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO agentflow_trace
                (id, start_time_utc, project_id, context_id, task_id, agentflow_id, node_id, node_kind, input, duration_milliseconds, status)
            VALUES
                ({rawTraceId}, {now}, {Guid.CreateVersion7()}, {"raw-trace"}, {Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {"raw-node"}, {"Input"}, {"input"}, {0L}, {(int)
                AgentflowNodeExecutionStatus.Succeeded})
            """,
            TestContext.Current.CancellationToken
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO project_conversation_turn
                (id, project_conversation_id, target_id, runtime_type, status, input_message_id, first_sequence, step_count, started_at)
            VALUES
                ({rawTurnId}, {Guid.CreateVersion7()}, {Guid.CreateVersion7()}, {"agentflow"}, {"accepted"}, {Guid.CreateVersion7()}, {2L}, {0}, {now})
            """,
            TestContext.Current.CancellationToken
        );

        var traceExtra = await context
            .Database.SqlQueryRaw<string>("SELECT extra AS Value FROM agentflow_trace WHERE id = {0}", traceId)
            .SingleAsync(TestContext.Current.CancellationToken);
        var turnExtra = await context
            .Database.SqlQueryRaw<string>("SELECT extra AS Value FROM project_conversation_turn WHERE id = {0}", turnId)
            .SingleAsync(TestContext.Current.CancellationToken);
        var rawTraceExtra = await context
            .Database.SqlQueryRaw<string>("SELECT extra AS Value FROM agentflow_trace WHERE id = {0}", rawTraceId)
            .SingleAsync(TestContext.Current.CancellationToken);
        var rawTurnExtra = await context
            .Database.SqlQueryRaw<string>(
                "SELECT extra AS Value FROM project_conversation_turn WHERE id = {0}",
                rawTurnId
            )
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("{\"agentflowRevision\":0}", traceExtra);
        Assert.Equal("{\"agentflowRevision\":0}", turnExtra);
        Assert.Equal("{}", rawTraceExtra);
        Assert.Equal("{}", rawTurnExtra);
    }
}
