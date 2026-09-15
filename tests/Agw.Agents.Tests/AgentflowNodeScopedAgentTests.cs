using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Context;
using Agw.Agents.Execution.Agentflows.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class AgentflowNodeScopedAgentTests
{
    [Fact]
    public async Task RunStreamingAsync_ConsumerStopsEarly_PersistsCapturedToolMessages()
    {
        var writer = new RecordingConversationHistoryWriter();
        var providerSessionState = new StubProviderSessionState();
        var scope = new AgentflowAgentSessionScope(
            providerSessionState,
            Guid.CreateVersion7(),
            "context-1",
            taskId: null,
            conversationHistoryWriter: writer
        );
        var agent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent(),
            "node-1",
            "Node 1",
            instructions: null,
            scope,
            agentflowId: Guid.CreateVersion7(),
            agentId: Guid.CreateVersion7()
        );

        await foreach (
            var _ in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "run")],
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            break;
        }

        var warning = Assert.Single(Assert.Single(writer.Calls));
        Assert.Equal(ToolMessageTypes.Warning, warning.AdditionalProperties!["type"]?.ToString());
        Assert.Equal("Node 1", warning.AdditionalProperties["nodeName"]?.ToString());
        Assert.Equal("Node 1", providerSessionState.NodeName);
    }

    [Fact]
    public async Task RunStreamingAsync_ConfiguredNodeName_AddsMetadataWithoutChangingAuthor()
    {
        var agent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent(),
            "node-1",
            "  Review Node  ",
            instructions: null,
            sessionScope: null
        );

        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "run")],
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.All(
            updates,
            update => Assert.Equal("Review Node", update.AdditionalProperties!["nodeName"]?.ToString())
        );
        Assert.Equal(ToolMessageTypes.Warning, updates[0].AdditionalProperties!["type"]?.ToString());
        Assert.Equal("general-agent", updates[1].AuthorName);
    }

    [Fact]
    public async Task RunStreamingAsync_NestedNodeScopes_PreservesInnermostNodeName()
    {
        var innerAgent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent(),
            "participant-node",
            "Participant Node",
            instructions: null,
            sessionScope: null
        );
        var outerAgent = new AgentflowNodeScopedAgent(
            innerAgent,
            "block-node",
            "Block Node",
            instructions: null,
            sessionScope: null
        );

        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in outerAgent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "run")],
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        Assert.All(
            updates,
            update => Assert.Equal("Participant Node", update.AdditionalProperties!["nodeName"]?.ToString())
        );
    }

    [Fact]
    public async Task RunAsync_ConfiguredNodeName_AddsMetadataAndPreservesExistingProperties()
    {
        var agent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent(),
            "node-1",
            "Review Node",
            instructions: null,
            sessionScope: null
        );

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "run")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var message = Assert.Single(response.Messages);
        Assert.Equal("general-agent", message.AuthorName);
        Assert.Equal("kept", message.AdditionalProperties!["marker"]?.ToString());
        Assert.Equal("Review Node", message.AdditionalProperties["nodeName"]?.ToString());
    }

    [Fact]
    public async Task RunAsync_ExistingNodeName_DoesNotOverwriteInnerNodeName()
    {
        var agent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent("Inner Node"),
            "node-1",
            "Outer Node",
            instructions: null,
            sessionScope: null
        );

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "run")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var message = Assert.Single(response.Messages);
        Assert.Equal("Inner Node", message.AdditionalProperties!["nodeName"]?.ToString());
    }

    [Fact]
    public async Task RunAsync_BlankNodeName_DoesNotAddMetadata()
    {
        var agent = new AgentflowNodeScopedAgent(
            new ToolMessageAgent(),
            "node-1",
            "   ",
            instructions: null,
            sessionScope: null
        );

        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "run")],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var message = Assert.Single(response.Messages);
        Assert.False(message.AdditionalProperties!.ContainsKey("nodeName"));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("user")]
    public async Task RunStreamingAsync_RepeatedNestedHandoff_ObservesOnlyPortableInputsWithFreshIds(string sourceRole)
    {
        // Arrange
        var observed = new List<ChatMessage>();
        var scope = new AgentflowAgentSessionScope(
            new StubProviderSessionState(),
            Guid.CreateVersion7(),
            "context",
            null
        )
        {
            InputObserver = (input, _) =>
            {
                observed.Add(input);
                return ValueTask.CompletedTask;
            },
        };
        var leaf = new AgentflowNodeScopedAgent(new ToolMessageAgent(), "leaf", "Leaf", "node instructions", scope);
        var nested = new AgentflowNodeScopedAgent(leaf, "nested", "Nested", null, scope, isWorkflow: true);
        var upstream = new ChatMessage(
            new ChatRole(sourceRole),
            [
                new TextContent("review"),
                new TextReasoningContent("private reasoning"),
                new FunctionCallContent("call", "tool"),
                new UsageContent(new UsageDetails { TotalTokenCount = 42 }),
            ]
        )
        {
            MessageId = "upstream",
            AuthorName = "pi",
            AdditionalProperties = new() { ["nodeName"] = "Review" },
        };
        var context = new ChatMessage(ChatRole.User, "injected context")
        {
            AdditionalProperties = new() { ["nodeName"] = "Context" },
        }.WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "test");
        var session = await nested.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act: the same upstream value can be consumed again on a later loop activation.
        for (var activation = 0; activation < 2; activation++)
        {
            await foreach (
                var update in nested.RunStreamingAsync(
                    [
                        new(ChatRole.User, "original"),
                        context,
                        upstream,
                        new(ChatRole.Tool, [new FunctionResultContent("orphan", "tool result")]),
                    ],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                Assert.NotEqual(ChatRole.User, update.Role);
        }

        // Assert: the nested container never publishes a second copy.
        Assert.Equal(2, observed.Count);
        Assert.NotEqual(observed[0].MessageId, observed[1].MessageId);
        Assert.All(
            observed,
            input =>
            {
                Assert.Equal("review", Assert.IsType<TextContent>(Assert.Single(input.Contents)).Text);
                Assert.Equal(ChatRole.User, input.Role);
                Assert.Equal("pi", input.AuthorName);
                Assert.Equal("Review", input.AdditionalProperties!["nodeName"]);
            }
        );
        Assert.Equal("upstream", upstream.MessageId);
        Assert.Equal(new ChatRole(sourceRole), upstream.Role);
        Assert.False(upstream.AdditionalProperties!.ContainsKey(ConversationHistoryMetadata.AgentflowInputKey));
    }

    private sealed class RecordingConversationHistoryWriter : IConversationHistoryWriter
    {
        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public Task AppendAsync(
            Guid projectId,
            string contextId,
            IReadOnlyList<ChatMessage> messages,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(messages.ToList());
            return Task.CompletedTask;
        }
    }

    private sealed class StubProviderSessionState : IProviderSessionState
    {
        public string? NodeName { get; private set; }

        public void InitializeSessionState(AgentSession session, string contextId, Guid projectId) { }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope
        ) { }

        public void InitializeSessionState(
            AgentSession session,
            string contextId,
            Guid projectId,
            string historyScope,
            string? nodeName
        )
        {
            NodeName = nodeName;
        }
    }

    private sealed class ToolMessageAgent : AIAgent
    {
        private readonly string? _nodeName;

        public ToolMessageAgent(string? nodeName = null)
        {
            _nodeName = nodeName;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ToolMessageSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new ToolMessageSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        )
        {
            var properties = new AdditionalPropertiesDictionary { ["marker"] = "kept" };
            if (_nodeName != null)
            {
                properties["nodeName"] = _nodeName;
            }

            return Task.FromResult(
                new AgentResponse([
                    new ChatMessage(ChatRole.Assistant, "done")
                    {
                        AuthorName = "general-agent",
                        AdditionalProperties = properties,
                    },
                ])
            );
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            yield return new AgentResponseUpdate(ChatRole.System, [new TextContent(string.Empty)])
            {
                AuthorName = "tools",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["type"] = ToolMessageTypes.Warning,
                    ["persistSeparately"] = true,
                },
            };
            yield return new AgentResponseUpdate(ChatRole.Assistant, "not consumed") { AuthorName = "general-agent" };
            await Task.CompletedTask;
        }

        private sealed class ToolMessageSession : AgentSession;
    }
}
