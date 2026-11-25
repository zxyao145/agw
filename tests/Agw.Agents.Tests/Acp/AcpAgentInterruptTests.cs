using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using static Agw.Agents.Tests.Acp.AcpAgentHarness;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentInterruptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunTurn_Interrupted_ResumesWithProcessSelectedByGracePeriod(bool ignoresCancel)
    {
        using var user = EnterUser();
        var first = new
        {
            ignoreCancel = ignoresCancel,
            steps = new object[]
            {
                Text("accepted-before-cancel"),
                new { delayMs = 30000 },
                Text("unwanted-after-cancel"),
            },
        };
        await using var agent = await ScenarioAsync(
            new { capabilities = new { resume = true }, turns = new object[] { first, Turn(Text("continue")) } },
            configure: settings => settings["AcpAgents:CancelGracePeriod"] = "00:00:01"
        );
        var turn = await agent.StartTurnAsync();
        await WaitUntilAsync(() =>
            agent
                .ReadMessages(turn)
                .Any(m => m.Contents.OfType<AgwTextContent>().Any(c => c.Content == "accepted-before-cancel"))
        );
        agent.Interrupt();
        await agent.WaitIdleAsync();
        Assert.Equal(ProjectConversationTurnStatus.Interrupted, (await agent.ReadTurnAsync(turn)).Status);
        Assert.Single(agent.Requests("session/cancel"));
        var history = await agent.ReadHistoryAsync();
        Assert.Contains(history, row => row.ConversationPayload?.Contains("accepted-before-cancel") == true);
        Assert.DoesNotContain(history, row => row.ConversationPayload?.Contains("unwanted-after-cancel") == true);
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Equal(ignoresCancel ? 2 : 1, agent.Events("start").Count);
        Assert.Equal(ignoresCancel ? 1 : 0, agent.Requests("session/resume").Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunTurn_ProcessOrProtocolFailure_ReportsStderrAndRestarts(bool exits)
    {
        using var user = EnterUser();
        object step = exits
            ? new { exit = 7, stderr = "fatal-process" }
            : new { stdout = "invalid", stderr = "fatal-protocol" };
        await using var agent = await ScenarioAsync(
            new { capabilities = new { resume = true }, turns = new[] { Turn(step), Turn(Text("recovered")) } }
        );
        var turn = await agent.RunTurnAsync();
        await agent.AssertFailureAsync(
            turn,
            ErrorCodes.AcpAgentRequestFailed.Code,
            exits ? "fatal-process" : "fatal-protocol"
        );
        if (exits)
            Assert.Contains(
                agent.ReadMessages(turn).SelectMany(m => m.Contents).OfType<AgwErrorContent>(),
                e => e.Content.Contains("7")
            );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Equal(2, agent.Events("start").Count);
        Assert.Single(agent.Requests("session/resume"));
    }
}
