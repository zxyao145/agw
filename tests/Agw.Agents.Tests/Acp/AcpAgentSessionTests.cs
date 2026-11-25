using System.Diagnostics;
using Agw.Shared.Exceptions;
using static Agw.Agents.Tests.Acp.AcpAgentHarness;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpAgentSessionTests
{
    [Fact]
    public async Task RunTurn_SecondTurn_ReusesSessionAndProcess()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { turns = new[] { Turn(Text("one")), Turn(Text("two")) } });
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Single(agent.Events("start"));
        var prompts = agent.Requests("session/prompt");
        Assert.Equal(2, prompts.Count);
        var id = await agent.ReadProviderSessionIdAsync();
        Assert.NotNull(id);
        Assert.All(prompts, p => Assert.Equal(id, p.GetProperty("sessionId").GetString()));
    }

    [Theory]
    [InlineData(true, "session/resume")]
    [InlineData(false, "session/load")]
    public async Task RunTurn_AfterRuntimeRebuild_RestoresSessionWithoutReplayingOutput(bool resume, string method)
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new
            {
                capabilities = new { resume, loadSession = true },
                replay = new[] { Chunk("replayed-content") },
                turns = new[] { Turn(Text("one")), Turn(Text("two")) },
            }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var id = await agent.ReadProviderSessionIdAsync();
        await agent.RebuildCoordinatorAsync();
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Equal(id, Assert.Single(agent.Requests(method)).GetProperty("sessionId").GetString());
        Assert.Equal(id, await agent.ReadProviderSessionIdAsync());
        Assert.DoesNotContain(
            await agent.ReadHistoryAsync(),
            row => row.ConversationPayload?.Contains("replayed-content") == true
        );
    }

    [Fact]
    public async Task RunTurn_ResumeNotFound_CreatesNewSession()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { capabilities = new { resume = true }, turns = new[] { Turn(Text("ok")) } }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var previous = await agent.ReadProviderSessionIdAsync();
        await agent.RebuildCoordinatorAsync();
        File.Delete(agent.LogPath + ".state.json");
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Equal(previous, Assert.Single(agent.Requests("session/resume")).GetProperty("sessionId").GetString());
        Assert.Equal(2, agent.Requests("session/new").Count);
        Assert.NotEqual(previous, await agent.ReadProviderSessionIdAsync());
    }

    [Fact]
    public async Task RunTurn_ResumeOtherError_FailsWithCode()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new
            {
                capabilities = new { resume = true },
                resumeError = new { code = -32045, message = "cannot recover" },
                turns = new[] { Turn(Text("ready")) },
            }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        await agent.RebuildCoordinatorAsync();
        await agent.AssertFailureAsync(await agent.RunTurnAsync(), ErrorCodes.AcpAgentRequestFailed.Code, "-32045");
        Assert.Single(agent.Requests("session/new"));
    }

    [Fact]
    public async Task RunTurn_AfterConversationCleared_StartsNewSession()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { capabilities = new { resume = true }, turns = new[] { Turn(Text("one")), Turn(Text("two")) } }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var previous = await agent.ReadProviderSessionIdAsync();
        await agent.ClearConversationAsync();
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        Assert.Equal(2, agent.Requests("session/new").Count);
        Assert.NotEqual(previous, await agent.ReadProviderSessionIdAsync());
    }

    [Fact]
    public async Task RunTurn_SessionIdTooLong_FailsWithRequestFailed()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(new { sessionId = new string('a', 201) });
        await agent.AssertFailureAsync(await agent.RunTurnAsync(), ErrorCodes.AcpAgentRequestFailed.Code);
        Assert.Null(await agent.ReadProviderSessionIdAsync());
    }

    [Fact]
    public async Task DisposeAsync_CloseCapability_SendsSessionClose()
    {
        using var user = EnterUser();
        await using var agent = await ScenarioAsync(
            new { capabilities = new { close = true }, turns = new[] { Turn(Text("ok")) } }
        );
        await agent.AssertCompletedAsync(await agent.RunTurnAsync());
        var pid = Assert.Single(agent.Events("start")).GetProperty("pid").GetInt32();
        using var process = Process.GetProcessById(pid);
        await agent.RebuildCoordinatorAsync();
        Assert.Equal(
            await agent.ReadProviderSessionIdAsync(),
            Assert.Single(agent.Requests("session/close")).GetProperty("sessionId").GetString()
        );
        Assert.True(process.HasExited);
    }
}
