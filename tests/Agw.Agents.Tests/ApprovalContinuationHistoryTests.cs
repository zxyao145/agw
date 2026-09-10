using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Tools;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed partial class AgentRequestContextAgentTests
{
    public static IEnumerable<object[]> ApprovalContinuationCases() =>
        from mode in Enum.GetValues<ConversationHistoryWriteMode>()
        from streaming in new[] { false, true }
        from compact in new[] { false, true }
        select new object[] { mode, streaming, compact };

    [Theory]
    [MemberData(nameof(ApprovalContinuationCases))]
    public async Task ApprovalContinuation_WithNewUserText_PreservesToolGroupsAndReasoningAfterReload(
        ConversationHistoryWriteMode mode,
        bool streaming,
        bool compact
    )
    {
        // Arrange
        using var owner = EnterHistoryUser();
        await using var fixture = await StreamingHistoryFixture.CreateAsync(mode);
        var model = new ApprovalContinuationChatClient();
        await using var capabilities = CreateCapabilities([
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => "result", "lookup")),
        ]);
        var definition = new ResolvedAgentDefinition
        {
            Id = "approval-test",
            Name = "Approval test",
            ModelId = "test-model",
            OpenTelemetrySourceName = "test",
            ChatHistoryProvider = fixture.Provider,
            CompactionProvider = compact
                ? new CompactionProvider(new ContextWindowCompactionStrategy(128000, 4096), stateKey: "approval-test")
                : null,
        };
        var agent = CreateAgent(
            model.AsAgwAgent(definition, capabilities, NullLoggerFactory.Instance, fixture.Services),
            fixture.Provider,
            memoryText: null
        );
        var session = await InitializeHistorySessionAsync(agent, fixture);
        var token = TestContext.Current.CancellationToken;
        var first = await RunAsync([new(ChatRole.User, "start")]);
        var approval = Assert.Single(first.OfType<ToolApprovalRequestContent>());
        session = await agent.DeserializeSessionAsync(
            await agent.SerializeSessionAsync(session, cancellationToken: token),
            cancellationToken: token
        );
        fixture.Provider.InitializeSessionState(session, "streaming", fixture.ProjectId);

        // Act
        await RunAsync([
            new(ChatRole.User, "continue with the approved tools"),
            new(ChatRole.User, [approval.CreateAlwaysApproveToolResponse()]),
        ]);
        await RunAsync([new(ChatRole.User, "follow-up")]);

        // Assert: both the live continuation and a later history read keep complete tool groups.
        Assert.Equal(3, model.Requests.Count);
        foreach (var request in model.Requests.Skip(1))
        {
            AssertValidToolGroups(request);
            var call = Assert.Single(request, message => message.Contents.OfType<FunctionCallContent>().Any());
            Assert.Equal("original reasoning", Assert.Single(call.Contents.OfType<TextReasoningContent>()).Text);
            Assert.Equal(2, request.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            Assert.Single(request, message => message.Text == "continue with the approved tools");
        }

        async Task<List<AIContent>> RunAsync(IReadOnlyList<ChatMessage> input)
        {
            await using var scope = ConversationHistoryPersistenceContext.BeginScope(
                fixture.Provider,
                fixture.ProjectId,
                "streaming",
                0,
                token
            );
            var messages = AgentflowMessageTransforms.ApplyInstructions(input, "Node instructions.");
            if (!streaming)
                return (await agent.RunAsync(messages, session, cancellationToken: token))
                    .Messages.SelectMany(message => message.Contents)
                    .ToList();
            var contents = new List<AIContent>();
            await foreach (var update in agent.RunStreamingAsync(messages, session, cancellationToken: token))
                contents.AddRange(update.Contents);
            return contents;
        }
    }

    private static void AssertValidToolGroups(IReadOnlyList<ChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (pending.Count > 0)
                Assert.Equal(ChatRole.Tool, message.Role);
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
                Assert.True(pending.Remove(result.CallId), $"Unmatched or duplicate result: {result.CallId}");
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.True(pending.Add(call.CallId));
            }
        }
        Assert.Empty(pending);
    }

    private sealed class ApprovalContinuationChatClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(messages.ToList());
            var content =
                Requests.Count == 1
                    ? new List<AIContent>
                    {
                        new TextReasoningContent("original reasoning"),
                        new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>()),
                        new FunctionCallContent("call-2", "lookup", new Dictionary<string, object?>()),
                    }
                    : [new TextContent("done")];
            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, content) { MessageId = $"response-{Requests.Count}" }
                )
            );
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
