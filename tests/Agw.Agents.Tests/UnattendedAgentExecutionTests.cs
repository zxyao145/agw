using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class UnattendedAgentExecutionTests
{
    [Fact]
    public async Task CollectStreamingMessagesAsync_ApprovalRequest_FailsExplicitly()
    {
        var agent = new UnattendedTestAgent(includeApproval: true);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var persistence = new ToolTurnPersistence(
            agent,
            session,
            (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<AgwException>(
            async () => await AgentRuntimeService.CollectStreamingMessagesAsync(
                agent,
                [new ChatMessage(ChatRole.User, "run a tool")],
                session,
                persistence,
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Contains("unattended", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectStreamingMessagesAsync_ToolMessage_PersistsBeforeReturning()
    {
        var agent = new UnattendedTestAgent(includeApproval: false);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<ChatMessage>? persisted = null;
        var persistence = new ToolTurnPersistence(
            agent,
            session,
            (messages, _) =>
            {
                persisted = messages.ToList();
                return Task.CompletedTask;
            });

        var messages = await AgentRuntimeService.CollectStreamingMessagesAsync(
            agent,
            [new ChatMessage(ChatRole.User, "run")],
            session,
            persistence,
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(messages);
        var warning = Assert.Single(persisted!);
        Assert.Equal(ToolMessageTypes.Warning, warning.AdditionalProperties!["type"]?.ToString());
    }

    private sealed class UnattendedTestAgent(bool includeApproval) : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new UnattendedTestSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new UnattendedTestSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.System, [new TextContent(string.Empty)])
            {
                AuthorName = "tools",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["type"] = ToolMessageTypes.Warning,
                    ["persistSeparately"] = true
                }
            };

            if (includeApproval)
            {
                yield return new AgentResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new ToolApprovalRequestContent(
                            "approval-1",
                            new FunctionCallContent(
                                "call-1",
                                "run_shell",
                                new Dictionary<string, object?>()))
                    ]);
                yield break;
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
        }

        private sealed class UnattendedTestSession : AgentSession;
    }
}
