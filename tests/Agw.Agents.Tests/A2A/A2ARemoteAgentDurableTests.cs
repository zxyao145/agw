using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using static Agw.Agents.Tests.A2A.A2ARemoteAgentStreamingTests;

namespace Agw.Agents.Tests.A2A;

public sealed class A2ARemoteAgentDurableTests
{
    public static bool PostgresEnabled => DurableTestConnections.PostgresConnectionString is not null;

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires the isolated PostgreSQL test database.")]
    [Trait("Category", "Postgres")]
    public async Task RunAgentSegment_A2AAgent_CompletesInOneSegment()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(
            server,
            postgres: DurableTestConnections.PostgresConnectionString
        );

        var turn = await harness.RunTurnAsync("distributed");

        Assert.Equal(ProjectConversationTurnStatus.Completed, (await harness.ReadTurnAsync(turn)).Status);
        var execution = await harness.QueryAsync(db =>
            db.DurableExecutions.AsNoTracking()
                .SingleAsync(row => row.Id == turn, TestContext.Current.CancellationToken)
        );
        Assert.Equal(DurableExecutionStatus.Completed, execution.Status);
        Assert.Equal(0, execution.SegmentIndex);
        Assert.Contains(
            await harness.ReadDurableMessagesAsync(turn),
            message =>
                A2ARemoteAgentExecutionTests.IsResult(message)
                && A2ARemoteAgentExecutionTests.HasText(message, "distributed")
        );
        Assert.NotNull((await harness.ReadSessionAsync()).ContextId);
    }

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires the isolated PostgreSQL test database.")]
    [Trait("Category", "Postgres")]
    public async Task RunAgentSegment_CodexAgent_StillRejected()
    {
        using var user = A2ARemoteAgentHarness.EnterUser();
        await using var server = await StartAsync(ScriptedAgentHandler.Echo());
        await using var harness = await A2ARemoteAgentHarness.CreateAsync(
            server,
            postgres: DurableTestConnections.PostgresConnectionString
        );
        var agentId = Guid.CreateVersion7();
        await harness.QueryAsync(db =>
        {
            db.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = "codex",
                    Type = AgentType.External,
                    ExternalAgentKind = EngineKind.Codex,
                    Extra = "{}",
                }
            );
            return db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        var turn = await harness.RunTurnAsync("distributed", agentId);

        Assert.Equal(ProjectConversationTurnStatus.Failed, (await harness.ReadTurnAsync(turn)).Status);
        Assert.Contains(
            (await harness.ReadDurableMessagesAsync(turn))
                .SelectMany(message => message.Contents)
                .OfType<AgwErrorContent>(),
            error =>
                error.Content.Contains(
                    "Distributed execution supports System Agents and A2A Agents.",
                    StringComparison.Ordinal
                )
        );
    }
}
