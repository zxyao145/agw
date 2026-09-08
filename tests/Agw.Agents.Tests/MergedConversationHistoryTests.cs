using Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;
using Agw.Agents.Execution.Agents.ExternalAgents.Pi;
using Agw.Projects.Domain.Services;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkAdapter_DirectEfProvider_PersistsOriginalInputAndResponseOnce(bool claude)
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        ChatHistoryProvider adapter = claude
            ? new ClaudeCodeChatHistoryProvider(fixture.Provider)
            : new PiChatHistoryProvider(fixture.Provider);
        Assert.Same(fixture.Provider, adapter.GetService<IConversationHistoryRequests>());
        var sdk = new HistoryNotifyingAgent(adapter);
        var agent = CreateAgent(sdk, adapter, "private memory");
        var session = await InitializeHistorySessionAsync(agent, fixture);

        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "original")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains("private memory", Assert.Single(sdk.RequestMessages).Text, StringComparison.Ordinal);
        var records = await fixture.ReadAsync();
        Assert.Equal(["original", "answer"], records.Select(record => record.GetText()));
        Assert.Equal(records[0].TaskId, records[1].TaskId);
        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await fixture.ReadAsync()).Count);
    }

    [Fact]
    public async Task SharedProvider_ParallelSessions_KeepsRequestsResponsesAndNodeScopesSeparate()
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(ConversationHistoryWriteMode.TurnEnd);
        var agent = new HistoryNotifyingAgent(historyProvider: null);
        var first = await InitializeHistorySessionAsync(agent, fixture);
        var second = await InitializeHistorySessionAsync(agent, fixture);
        fixture.Provider.InitializeSessionState(first, "streaming", fixture.ProjectId, "first", "First node");
        fixture.Provider.InitializeSessionState(second, "streaming", fixture.ProjectId, "second", "Second node");
        await using (
            ConversationHistoryPersistenceContext.BeginScope(
                fixture.Provider,
                fixture.ProjectId,
                "streaming",
                0,
                TestContext.Current.CancellationToken
            )
        )
        {
            fixture.Provider.StageRequest(first, [new ChatMessage(ChatRole.User, "first request")]);
            fixture.Provider.StageRequest(second, [new ChatMessage(ChatRole.User, "second request")]);
            await Task.WhenAll(
                fixture
                    .Provider.InvokedAsync(
                        new ChatHistoryProvider.InvokedContext(
                            agent,
                            first,
                            [],
                            [new ChatMessage(ChatRole.Assistant, "first answer")]
                        ),
                        TestContext.Current.CancellationToken
                    )
                    .AsTask(),
                fixture
                    .Provider.InvokedAsync(
                        new ChatHistoryProvider.InvokedContext(
                            agent,
                            second,
                            [],
                            [new ChatMessage(ChatRole.Assistant, "second answer")]
                        ),
                        TestContext.Current.CancellationToken
                    )
                    .AsTask()
            );
            await fixture.Provider.PersistPendingAsync(agent, first, TestContext.Current.CancellationToken);
            await fixture.Provider.PersistPendingAsync(agent, second, TestContext.Current.CancellationToken);
            Assert.Empty(await fixture.ReadAsync());
            var firstHistory = await fixture.Provider.InvokingAsync(
                new ChatHistoryProvider.InvokingContext(agent, first, []),
                TestContext.Current.CancellationToken
            );
            var secondHistory = await fixture.Provider.InvokingAsync(
                new ChatHistoryProvider.InvokingContext(agent, second, []),
                TestContext.Current.CancellationToken
            );
            Assert.Equal(["first request", "first answer"], firstHistory.Select(message => message.Text));
            Assert.Equal(["second request", "second answer"], secondHistory.Select(message => message.Text));
        }
        var records = await fixture.ReadAsync();
        Assert.Equal(4, records.Count);
        Assert.Equal(2, records.GroupBy(record => record.TaskId).Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalRequestSave_Fails_ReportsFailureAndReleasesStagedInput(bool executionFails)
    {
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync();
        var failure = new InvalidOperationException("SDK failed");
        var sdk = new HistoryNotifyingAgent(null, executionFails ? failure : null);
        var agent = CreateAgent(sdk, fixture.Provider, memoryText: null);
        var session = await InitializeHistorySessionAsync(agent, fixture);
        fixture.FailNextContext = true;
        Task Run() =>
            agent.RunAsync(
                [new ChatMessage(ChatRole.User, "original")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            );

        if (executionFails)
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(Run));
        else
            await Assert.ThrowsAsync<DbUpdateException>(Run);

        await fixture.Provider.PersistPendingAsync(agent, session, TestContext.Current.CancellationToken);
        Assert.Empty(await fixture.ReadAsync());
    }
}
