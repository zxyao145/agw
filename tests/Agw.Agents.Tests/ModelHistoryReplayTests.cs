using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Messaging;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

/// <summary>
/// 真实 SDK、历史存储与压缩管线下的模型历史重放：工具循环、会话重新加载与审批延续。
/// Model history replay over the real SDK, history store and compaction pipeline: tool loops, session reloads and approval continuations.
/// </summary>
public sealed class ModelHistoryReplayTests
{
    [Theory]
    [InlineData(ConversationHistoryWriteMode.Immediate, false, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Immediate, false, "")]
    [InlineData(ConversationHistoryWriteMode.Immediate, true, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Immediate, true, "")]
    [InlineData(ConversationHistoryWriteMode.Interval, false, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Interval, false, "")]
    [InlineData(ConversationHistoryWriteMode.Interval, true, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.Interval, true, "")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, false, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, false, "")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, true, "original reasoning")]
    [InlineData(ConversationHistoryWriteMode.TurnEnd, true, "")]
    public async Task AnthropicHistory_ToolLoopAndSessionReload_PreserveThinking(
        ConversationHistoryWriteMode mode,
        bool streaming,
        string thinkingText
    )
    {
        // Arrange: exercise the real SDK, persistence and compaction pipeline over a fake HTTP transport.
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(mode);
        await using var model = new AnthropicReasoningChatClientTests.ClientFixture(
            "https://api.deepseek.com/anthropic"
        );
        model.Handler.RequireThinking = true;
        model.Handler.ReturnToolCall = true;
        model.Handler.ThinkingText = thinkingText;
        await using var capabilities = CreateCapabilities([AIFunctionFactory.Create(() => "result", "lookup")]);
        var history = fixture.CreateProvider();
        var definition = new ResolvedAgentDefinition
        {
            Id = "anthropic-thinking-agent",
            Name = "Thinking agent",
            ModelId = "thinking-model",
            OpenTelemetrySourceName = "test",
            ChatHistoryProvider = history,
            CompactionProvider = new CompactionProvider(new ContextWindowCompactionStrategy(100_000, 10_000)),
        };
        var agent = HistoryTestFixture.Record(
            model.Client.AsAgwAgent(definition, capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        var token = TestContext.Current.CancellationToken;
        var session = await fixture.CreateSessionAsync(agent);
        await new ConversationHistoryWriter(fixture.Store, fixture.Clock).AppendAsync(
            fixture.ProjectId,
            HistoryTestFixture.ContextId,
            [new(ChatRole.User, "old question"), new(ChatRole.Assistant, "old answer")],
            token
        );

        // Act
        await RunAsync("question");
        session = await agent.DeserializeSessionAsync(
            await agent.SerializeSessionAsync(session, cancellationToken: token),
            cancellationToken: token
        );
        await RunAsync("next question");

        // Assert: both the immediate tool continuation and persisted replay carry the original block.
        Assert.Equal(3, model.Handler.Requests.Count);
        foreach (var request in model.Handler.Requests.Skip(1))
        {
            var blocks = request
                .GetProperty("messages")
                .EnumerateArray()
                .SelectMany(message => message.GetProperty("content").EnumerateArray())
                .ToList();
            var thinking = Assert.Single(
                blocks,
                block =>
                    block.GetProperty("type").GetString() == "thinking"
                    && block.GetProperty("thinking").GetString() == thinkingText
                    && block.GetProperty("signature").GetString() == "original-signature"
            );
            Assert.Equal("original-signature", thinking.GetProperty("signature").GetString());
            Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_use");
            Assert.Single(blocks, block => block.GetProperty("type").GetString() == "tool_result");
        }
        var stored = (await fixture.ReadAsync())
            .Select(row =>
                JsonSerializer.Deserialize<ChatMessage>(
                    row.ConversationPayload!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )!
            )
            .ToList();
        Assert.Empty(
            Assert.Single(stored, message => message.Text == "old answer").Contents.OfType<TextReasoningContent>()
        );
        var reasoning = Assert.Single(
            stored.SelectMany(message => message.Contents).OfType<TextReasoningContent>(),
            content => content.Text == thinkingText
        );
        Assert.Equal("original-signature", reasoning.ProtectedData);

        async Task RunAsync(string text)
        {
            await using var turn = fixture.BeginTurn();
            if (streaming)
            {
                await foreach (
                    var _ in agent.RunStreamingAsync(
                        [new ChatMessage(ChatRole.User, text)],
                        session,
                        cancellationToken: token
                    )
                ) { }
            }
            else
                await agent.RunAsync([new ChatMessage(ChatRole.User, text)], session, cancellationToken: token);
        }
    }

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
        using var owner = HistoryTestFixture.EnterUser();
        await using var fixture = await HistoryTestFixture.CreateAsync(mode);
        var model = new ApprovalContinuationChatClient();
        await using var capabilities = CreateCapabilities([
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => "result", "lookup")),
        ]);
        var history = fixture.CreateProvider();
        var definition = new ResolvedAgentDefinition
        {
            Id = "approval-test",
            Name = "Approval test",
            ModelId = "test-model",
            OpenTelemetrySourceName = "test",
            ChatHistoryProvider = history,
            CompactionProvider = compact
                ? new CompactionProvider(new ContextWindowCompactionStrategy(128000, 4096), stateKey: "approval-test")
                : null,
        };
        var agent = HistoryTestFixture.Record(
            model.AsAgwAgent(definition, capabilities, NullLoggerFactory.Instance, fixture.Services),
            history
        );
        var session = await fixture.CreateSessionAsync(agent);
        var token = TestContext.Current.CancellationToken;
        var first = await RunAsync([new(ChatRole.User, "start")]);
        var approvals = first.OfType<ToolApprovalRequestContent>().ToList();
        Assert.Equal(2, approvals.Count);
        session = await agent.DeserializeSessionAsync(
            await agent.SerializeSessionAsync(session, cancellationToken: token),
            cancellationToken: token
        );

        // Act
        await RunAsync([
            new(ChatRole.User, "continue with the approved tools"),
            new(
                ChatRole.User,
                approvals
                    .Select(approval =>
                        MafApprovalAdapter.CreateResponse(
                            approval,
                            new ToolApprovalDecision
                            {
                                InteractionId = approval.RequestId,
                                Approved = true,
                                Scope = ApprovalScope.AlwaysTool,
                            }
                        )
                    )
                    .ToList()
            ),
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
            await using var turn = fixture.BeginTurn();
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

    private static AgentCapabilityComposition CreateCapabilities(IReadOnlyList<AITool> tools) =>
        new(
            tools: tools,
            pluginSkills: [],
            warnings: [],
            contextProviders: [],
            loopEvaluators: [],
            autoApprovalRules: [],
            planModeAllowedToolNames: new HashSet<string>(),
            toolWarnings: [],
            toolInvocationWarnings: new Dictionary<string, string>(),
            lease: new AgentResourceLease()
        );

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
