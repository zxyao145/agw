using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.MAF.Tests;

public sealed class PiAgentAIAgentTests
{
    [Fact]
    public async Task SerializeSessionAsync_WithStateBag_RoundTripsSessionAndState()
    {
        // Arrange
        await using var agent = new PiAgentAIAgent(
            new PiAgentAIAgentOptions { SessionId = "session-1", IsResume = true }
        );
        var session = Assert.IsType<PiAgentSession>(
            await agent.CreateSessionAsync(TestContext.Current.CancellationToken)
        );
        session.StateBag.SetValue("test", "value");

        // Act
        var serialized = await agent.SerializeSessionAsync(
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var restored = Assert.IsType<PiAgentSession>(
            await agent.DeserializeSessionAsync(serialized, cancellationToken: TestContext.Current.CancellationToken)
        );

        // Assert
        Assert.Equal("session-1", restored.SessionId);
        Assert.True(restored.StateBag.TryGetValue<string>("test", out var stateValue));
        Assert.Equal("value", stateValue);
    }

    [Fact]
    public async Task RunStreamingAsync_PersistsRequestAndAuthoritativeTurnOnly()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => EmitRun(transport, line);
        var piAgent = new PiAgent(
            new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );
        var history = new RecordingHistoryProvider();
        await using var agent = new PiAgentAIAgent(
            new PiAgentAIAgentOptions { ChatHistoryProvider = history },
            logger: null,
            piAgent
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            updates.Add(update);
        }

        // Assert
        Assert.Equal(2, history.Stored.Count);
        Assert.Single(history.Stored[0].Requests);
        Assert.Empty(history.Stored[0].Responses);
        Assert.Empty(history.Stored[1].Requests);
        var assistant = Assert.Single(history.Stored[1].Responses);
        Assert.Equal("done", assistant.Text);
        Assert.False(assistant.AdditionalProperties!.ContainsKey("agentName"));
        Assert.Equal("deepseek-v4-flash-vision-exp", assistant.AdditionalProperties!["modelName"]);
        var textUpdate = Assert.Single(
            updates,
            update => update.Contents.OfType<TextContent>().Any(content => content.Text == "do")
        );
        Assert.False(textUpdate.AdditionalProperties!.ContainsKey("agentName"));
        Assert.Equal("deepseek-v4-flash-vision-exp", textUpdate.AdditionalProperties!["modelName"]);
    }

    [Fact]
    public async Task RunAsync_ProviderReportsReasoning_MapsReasoningUsage()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => EmitRun(transport, line);
        var piAgent = new PiAgent(
            new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );
        await using var agent = new PiAgentAIAgent(new PiAgentAIAgentOptions(), logger: null, piAgent);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(1, response.Usage.ReasoningTokenCount);
    }

    [Fact]
    public async Task RunStreamingAsync_AcrossToolTurn_PreservesLogicalMessageOrderWithoutProgressText()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => EmitInterleavedToolRun(transport, line);
        var piAgent = new PiAgent(
            new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );
        var history = new RecordingHistoryProvider();
        await using var agent = new PiAgentAIAgent(
            new PiAgentAIAgentOptions { ChatHistoryProvider = history },
            logger: null,
            piAgent
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "inspect")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
        {
            updates.Add(update);
        }

        // Assert
        Assert.Collection(
            updates,
            update => Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents)),
            update => Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents)),
            update => Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents)),
            update => Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents))
        );
        Assert.All(updates, update => Assert.False(string.IsNullOrWhiteSpace(update.MessageId)));
        Assert.Equal(updates[0].ResponseId, updates[1].ResponseId);
        Assert.Equal(updates[0].ResponseId, updates[2].ResponseId);
        Assert.Equal(updates[0].ResponseId, updates[3].ResponseId);
        Assert.NotEqual(updates[0].MessageId, updates[1].MessageId);
        Assert.NotEqual(updates[0].MessageId, updates[2].MessageId);
        Assert.Equal(updates[2].MessageId, updates[3].MessageId);
        var persistedResponses = history.Stored.SelectMany(call => call.Responses).ToList();
        Assert.Equal(3, persistedResponses.Count);
        Assert.All(persistedResponses, message => Assert.False(string.IsNullOrWhiteSpace(message.MessageId)));
        Assert.Equal(3, persistedResponses.Select(message => message.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task RunAsync_CompactionFailure_ReportsUsageOnceAndKeepsErrorMessageFocused()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => EmitCompactionFailure(transport, line);
        var piAgent = new PiAgent(
            new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );
        await using var agent = new PiAgentAIAgent(new PiAgentAIAgentOptions(), logger: null, piAgent);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "compact")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(12, response.Usage.TotalTokenCount);
        var errorMessage = Assert.Single(response.Messages);
        Assert.IsType<ErrorContent>(Assert.Single(errorMessage.Contents));
    }

    [Fact]
    public async Task RunAsync_FailedTool_ReportsAuthoritativeErrorOnce()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => EmitFailedToolRun(transport, line);
        var piAgent = new PiAgent(
            new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );
        await using var agent = new PiAgentAIAgent(new PiAgentAIAgentOptions(), logger: null, piAgent);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "fail")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        var errors = response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>().ToList();
        var error = Assert.Single(errors);
        Assert.Equal("Pi tool 'bash' failed.", error.Message);
        var authoritativeToolMessage = Assert.Single(
            response.Messages,
            message => message.Contents.OfType<FunctionResultContent>().Any()
        );
        Assert.Contains(authoritativeToolMessage.Contents, content => content is ErrorContent);
    }

    [Fact]
    public async Task DisposeAsync_AfterRun_DisposesLivePiSessionOnce()
    {
        // Arrange
        var transport = new FakePiTransport();
        transport.OnWrite = line => EmitRun(transport, line);
        var piAgent = new PiAgent(
            new PiAgentOptions { CommandTimeout = TimeSpan.FromSeconds(2) },
            logger: null,
            (_, _) => transport
        );
        var agent = new PiAgentAIAgent(new PiAgentAIAgentOptions(), logger: null, piAgent);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await foreach (
            var _ in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "run")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        ) { }

        // Act
        await agent.DisposeAsync();
        await agent.DisposeAsync();

        // Assert
        Assert.Equal(1, transport.DisposeCount);
    }

    [Fact]
    public void Constructor_NonPositiveHistoryPersistenceTimeout_Throws()
    {
        // Arrange
        var options = new PiAgentAIAgentOptions { HistoryPersistenceTimeout = TimeSpan.Zero };

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new PiAgentAIAgent(options));
    }

    [Fact]
    public async Task RunAsync_HistoryPersistenceExceedsIndependentTimeout_ThrowsTimeoutException()
    {
        // Arrange
        var history = new BlockingHistoryProvider();
        await using var agent = new PiAgentAIAgent(
            new PiAgentAIAgentOptions
            {
                ChatHistoryProvider = history,
                HistoryPersistenceTimeout = TimeSpan.FromMilliseconds(30),
            }
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        try
        {
            // Act & Assert
            await Assert.ThrowsAsync<TimeoutException>(() =>
                agent.RunAsync(
                    [new ChatMessage(ChatRole.User, "persist")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
        }
        finally
        {
            history.Release();
        }
    }

    private static void EmitRun(FakePiTransport transport, string line)
    {
        var command = JsonDocument.Parse(line).RootElement;
        var type = command.GetProperty("type").GetString();
        var id = command.GetProperty("id").GetString();
        if (type == "get_state")
        {
            transport.Emit(
                $"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"get_state\",\"success\":true,\"data\":{{\"sessionId\":\"session-1\"}}}}"
            );
            return;
        }

        if (type != "prompt")
        {
            return;
        }

        transport.Emit($"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"prompt\",\"success\":true}}");
        transport.Emit(
            """{"type":"message_start","message":{"role":"assistant","content":[],"provider":"deepseek","model":"deepseek-v4-flash-vision-exp","timestamp":1}}"""
        );
        transport.Emit(
            """{"type":"message_update","usage":{"input":1,"output":1,"cacheRead":0,"cacheWrite":0,"totalTokens":2},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"do"}}"""
        );
        transport.Emit(
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"provider":"deepseek","model":"deepseek-v4-flash-vision-exp","usage":{"input":2,"output":1,"cacheRead":0,"cacheWrite":0,"reasoning":1,"totalTokens":3},"stopReason":"stop","timestamp":1},"toolResults":[]}"""
        );
        transport.Emit("""{"type":"agent_settled"}""");
    }

    private static void EmitInterleavedToolRun(FakePiTransport transport, string line)
    {
        var command = JsonDocument.Parse(line).RootElement;
        var type = command.GetProperty("type").GetString();
        var id = command.GetProperty("id").GetString();
        if (type == "get_state")
        {
            transport.Emit(
                $"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"get_state\",\"success\":true,\"data\":{{\"sessionId\":\"session-1\"}}}}"
            );
            return;
        }

        if (type != "prompt")
        {
            return;
        }

        transport.Emit($"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"prompt\",\"success\":true}}");
        transport.Emit("""{"type":"message_start","message":{"role":"assistant","content":[],"timestamp":1}}""");
        transport.Emit(
            """{"type":"message_update","assistantMessageEvent":{"type":"toolcall_end","contentIndex":0,"toolCall":{"type":"toolCall","id":"call-1","name":"read","arguments":{"path":"README.md"}}}}"""
        );
        transport.Emit(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"toolCall","id":"call-1","name":"read","arguments":{"path":"README.md"}}],"stopReason":"toolUse","timestamp":1}}"""
        );
        transport.Emit(
            """{"type":"tool_execution_start","toolCallId":"call-1","toolName":"read","args":{"path":"README.md"}}"""
        );
        transport.Emit(
            """{"type":"tool_execution_update","toolCallId":"call-1","toolName":"read","args":{"path":"README.md"},"partialResult":{"content":[{"type":"text","text":"partial"}]}}"""
        );
        transport.Emit(
            """{"type":"tool_execution_end","toolCallId":"call-1","toolName":"read","result":{"content":[{"type":"text","text":"done"}]},"isError":false}"""
        );
        transport.Emit(
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"toolCall","id":"call-1","name":"read","arguments":{"path":"README.md"}}],"stopReason":"toolUse","timestamp":1},"toolResults":[{"role":"toolResult","toolCallId":"call-1","toolName":"read","content":[{"type":"text","text":"done"}],"isError":false,"timestamp":2}]}"""
        );
        transport.Emit("""{"type":"message_start","message":{"role":"assistant","content":[],"timestamp":3}}""");
        transport.Emit(
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"Continue "}}"""
        );
        transport.Emit(
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"after the tool."}}"""
        );
        transport.Emit(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Continue after the tool."}],"stopReason":"stop","timestamp":3}}"""
        );
        transport.Emit(
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Continue after the tool."}],"stopReason":"stop","timestamp":3},"toolResults":[]}"""
        );
        transport.Emit("""{"type":"agent_settled"}""");
    }

    private static void EmitCompactionFailure(FakePiTransport transport, string line)
    {
        var command = JsonDocument.Parse(line).RootElement;
        var type = command.GetProperty("type").GetString();
        var id = command.GetProperty("id").GetString();
        if (type == "get_state")
        {
            transport.Emit(
                $"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"get_state\",\"success\":true,\"data\":{{\"sessionId\":\"session-1\"}}}}"
            );
            return;
        }

        if (type != "prompt")
        {
            return;
        }

        transport.Emit($"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"prompt\",\"success\":true}}");
        transport.Emit(
            """{"type":"compaction_end","result":{"summary":"partial","tokensBefore":20,"estimatedTokensAfter":10,"usage":{"input":10,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":12}},"errorMessage":"compaction failed"}"""
        );
        transport.Emit("""{"type":"agent_settled"}""");
    }

    private static void EmitFailedToolRun(FakePiTransport transport, string line)
    {
        var command = JsonDocument.Parse(line).RootElement;
        var type = command.GetProperty("type").GetString();
        var id = command.GetProperty("id").GetString();
        if (type == "get_state")
        {
            transport.Emit(
                $"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"get_state\",\"success\":true,\"data\":{{\"sessionId\":\"session-1\"}}}}"
            );
            return;
        }

        if (type != "prompt")
        {
            return;
        }

        transport.Emit($"{{\"type\":\"response\",\"id\":\"{id}\",\"command\":\"prompt\",\"success\":true}}");
        transport.Emit("""{"type":"message_start","message":{"role":"assistant","content":[],"timestamp":1}}""");
        transport.Emit(
            """{"type":"message_update","assistantMessageEvent":{"type":"toolcall_end","contentIndex":0,"toolCall":{"type":"toolCall","id":"call-1","name":"bash","arguments":{"command":"false"}}}}"""
        );
        transport.Emit(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"toolCall","id":"call-1","name":"bash","arguments":{"command":"false"}}],"stopReason":"toolUse","timestamp":1}}"""
        );
        transport.Emit(
            """{"type":"tool_execution_end","toolCallId":"call-1","toolName":"bash","result":{"content":[{"type":"text","text":"permission denied"}]},"isError":true}"""
        );
        transport.Emit(
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"toolCall","id":"call-1","name":"bash","arguments":{"command":"false"}}],"stopReason":"toolUse","timestamp":1},"toolResults":[{"role":"toolResult","toolCallId":"call-1","toolName":"bash","content":[{"type":"text","text":"permission denied"}],"isError":true,"timestamp":2}]}"""
        );
        transport.Emit("""{"type":"agent_settled"}""");
    }

    private sealed class RecordingHistoryProvider : ChatHistoryProvider
    {
        public List<StoredCall> Stored { get; } = [];

        protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IEnumerable<ChatMessage>>([]);

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default
        )
        {
            Stored.Add(new StoredCall(context.RequestMessages.ToList(), context.ResponseMessages?.ToList() ?? []));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingHistoryProvider : ChatHistoryProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<IEnumerable<ChatMessage>>([]);

        protected override ValueTask InvokedCoreAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default
        ) => new(_release.Task);

        public void Release() => _release.TrySetResult();
    }

    private sealed class StoredCall
    {
        public StoredCall(IReadOnlyList<ChatMessage> requests, IReadOnlyList<ChatMessage> responses)
        {
            Requests = requests;
            Responses = responses;
        }

        public IReadOnlyList<ChatMessage> Requests { get; }

        public IReadOnlyList<ChatMessage> Responses { get; }
    }

    private sealed class FakePiTransport : IPiProcessTransport
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<PiProcessExitInfo> _exit = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Action<string>? OnWrite { get; set; }

        public int DisposeCount { get; private set; }

        public string StandardErrorTail => string.Empty;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            OnWrite?.Invoke(line);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadLinesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken))
            {
                yield return line;
            }
        }

        public Task<PiProcessExitInfo> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public ValueTask KillAsync(CancellationToken cancellationToken)
        {
            _exit.TrySetResult(new PiProcessExitInfo { ExitCode = -1 });
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _exit.TrySetResult(new PiProcessExitInfo { ExitCode = 0 });
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void Emit(string line) => _output.Writer.TryWrite(line);
    }
}
