using System.Diagnostics;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using static Agw.Agents.Tests.Acp.AcpAgentHarness;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentDurableTests
{
    public static bool PostgresEnabled => !string.IsNullOrWhiteSpace(DurableTestConnections.PostgresConnectionString);

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires the isolated PostgreSQL test database.")]
    [Trait("Category", "Postgres")]
    public async Task RunTurn_DistributedAcpAgent_CompletesInOneSegment()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { capabilities = new { resume = true }, turns = new[] { Turn(Text("first")), Turn(Text("second")) } },
            postgres: DurableTestConnections.PostgresConnectionString
        );
        foreach (var text in new[] { "one", "two" })
        {
            var id = await agent.RunTurnAsync(text, AgwPermissionMode.FullAccess);
            Assert.Equal(ProjectConversationTurnStatus.Completed, (await agent.ReadTurnAsync(id)).Status);
            var durable = await agent.Runtime.QueryAsync(db =>
                db.DurableExecutions.AsNoTracking().SingleAsync(x => x.Id == id, Ct)
            );
            Assert.Equal(DurableExecutionStatus.Completed, durable.Status);
            Assert.Equal(0, durable.SegmentIndex);
            Assert.Throws<ArgumentException>(() =>
                Process.GetProcessById(agent.Events("start")[^1].GetProperty("pid").GetInt32())
            );
        }
        Assert.Equal(2, agent.Events("start").Select(e => e.GetProperty("pid").GetInt32()).Distinct().Count());
        Assert.Equal(
            await agent.ReadProviderSessionIdAsync(),
            Assert.Single(agent.Requests("session/resume")).GetProperty("sessionId").GetString()
        );
    }

    [Fact(SkipUnless = nameof(PostgresEnabled), Skip = "Requires the isolated PostgreSQL test database.")]
    [Trait("Category", "Postgres")]
    public async Task RunTurn_DistributedAlwaysAsk_ReturnsInvalidParam()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { }, postgres: DurableTestConnections.PostgresConnectionString);
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            agent.StartTurnAsync(permission: AgwPermissionMode.AlwaysAsk)
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Empty(agent.Events("start"));
    }
}
