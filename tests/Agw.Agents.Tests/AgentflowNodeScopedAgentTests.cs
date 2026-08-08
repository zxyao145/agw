using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agentflows;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Projects;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class AgentflowNodeScopedAgentTests
{
    [Fact]
    public async Task RunStreamingAsync_ConsumerStopsEarly_PersistsCapturedToolMessages()
    {
        var writer = new RecordingConversationHistoryWriter();
        var scope = new AgentflowAgentSessionScope(
            new StubProviderSessionState(),
            Guid.CreateVersion7(),
            "context-1",
            taskId: null,
            conversationHistoryWriter: writer);
        var agent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent(),
            "node-1",
            "Node 1",
            instructions: null,
            scope,
            agentId: Guid.CreateVersion7());

        await foreach (var _ in agent.RunStreamingAsync(
                           [new ChatMessage(ChatRole.User, "run")],
                           cancellationToken: TestContext.Current.CancellationToken))
        {
            break;
        }

        var warning = Assert.Single(Assert.Single(writer.Calls));
        Assert.Equal(ToolMessageTypes.Warning, warning.AdditionalProperties!["type"]?.ToString());
    }

    private sealed class RecordingConversationHistoryWriter : IConversationHistoryWriter
    {
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public Task AppendAsync(
            Guid projectId,
            string contextId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            return Task.CompletedTask;
        }
    }

    private sealed class StubProviderSessionState : IProviderSessionState
    {
        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId)
        {
        }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope)
        {
        }
    }

    private sealed class ToolMessageAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ToolMessageSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ToolMessageSession());

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
            yield return new AgentResponseUpdate(ChatRole.System, [new TextContent(string.Empty)])
            {
                AuthorName = "tools",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["type"] = ToolMessageTypes.Warning,
                    ["persistSeparately"] = true
                }
            };
            yield return new AgentResponseUpdate(ChatRole.Assistant, "not consumed");
            await Task.CompletedTask;
        }

        private sealed class ToolMessageSession : AgentSession;
    }
}
