using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Steering;
using Agw.Agents.Execution.Turns;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public class SteerDeliveryTests
{
    [Fact]
    public async Task InjectionClient_PendingSteer_IsAppendedAfterRequestAndReportedToClient()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink);
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var steerId = Guid.CreateVersion7();
        inbox.Offer(steerId, Input("change direction"));
        var inner = new RecordingChatClient("ok");
        using var client = new SteerInjectionChatClient(inner);
        using var _ = scope.Push();

        await Drain(
            client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "original")],
                null,
                TestContext.Current.CancellationToken
            )
        );

        var request = Assert.Single(inner.Requests);
        Assert.Equal(["original", "change direction"], request.Select(message => message.Text));
        Assert.Equal(steerId.ToString("D"), request[^1].MessageId);
        var userMessage = Assert.Single(sink.Messages, message => message.Role == AiRole.User);
        Assert.Equal(steerId.ToString("D"), userMessage.MessageId);
        Assert.Equal(scope.Context.TurnId.ToString("D"), userMessage.AdditionalProperties!["turnId"]);
        var delivered = Assert.Single(sink.Messages, message => IsType(message, AgwMessageTypes.SteerDelivered));
        Assert.Equal(steerId.ToString("D"), delivered.AdditionalProperties!["steerId"]);
        Assert.True(sink.Messages.IndexOf(userMessage) < sink.Messages.IndexOf(delivered));
    }

    [Fact]
    public async Task InjectionClient_NothingPending_LeavesRequestUntouched()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink);
        scope.BindSteers(new SteerInbox(8));
        var inner = new RecordingChatClient("ok");
        using var client = new SteerInjectionChatClient(inner);
        using var _ = scope.Push();

        await Drain(
            client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "original")],
                null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(["original"], Assert.Single(inner.Requests).Select(message => message.Text));
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task InjectionClient_ScopeWithoutInbox_IgnoresPendingSteers()
    {
        var scope = ExecutionTestScopes.Scope();
        var inner = new RecordingChatClient("ok");
        using var client = new SteerInjectionChatClient(inner);
        using var _ = scope.Push();

        await Drain(
            client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "original")],
                null,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(["original"], Assert.Single(inner.Requests).Select(message => message.Text));
    }

    [Fact]
    public async Task InjectionClient_Cancelled_ReturnsTakenSteerToClient()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink);
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var steerId = Guid.CreateVersion7();
        inbox.Offer(steerId, Input("lost?"));
        using var cancellation = new CancellationTokenSource();
        sink.OnWrite = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };
        using var client = new SteerInjectionChatClient(new RecordingChatClient("ok"));
        using var _ = scope.Push();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Drain(
                client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "original")], null, cancellation.Token)
            )
        );

        sink.OnWrite = null;
        var returned = Assert.Single(sink.Messages, message => IsType(message, AgwMessageTypes.SteerReturned));
        Assert.Equal(steerId.ToString("D"), returned.AdditionalProperties!["steerId"]);
        Assert.Equal("lost?", Assert.IsType<AgwTextContent>(Assert.Single(returned.Contents)).Content);
    }

    [Fact]
    public async Task TurnExecutor_SteerArrivesDuringFinalResponse_ContinuesSameTurn()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink);
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var steerId = Guid.CreateVersion7();
        var chat = new RecordingChatClient("first answer", "second answer")
        {
            OnRequest = index =>
            {
                if (index == 0)
                    inbox.Offer(steerId, Input("also check the tests"));
            },
        };
        var runtime = await CreateRuntimeAsync(chat);

        var output = await RunAsync(scope, runtime, Input("start"));

        Assert.Equal(2, chat.Requests.Count);
        Assert.Contains(
            chat.Requests[1],
            message => message.Role == ChatRole.User && message.Text == "also check the tests"
        );
        Assert.Contains(sink.Messages, message => IsType(message, AgwMessageTypes.SteerDelivered));
        Assert.DoesNotContain(sink.Messages, message => IsType(message, AgwMessageTypes.SteerReturned));
        Assert.Equal(
            ["first answer", "second answer"],
            output.Where(message => message.Role == AiRole.Assistant).Select(ReadText)
        );
        Assert.Equal(SteerOfferResult.Closed, inbox.Offer(Guid.CreateVersion7(), Input("too late")));
    }

    [Fact]
    public async Task TurnExecutor_NoSteer_EndsAfterFirstResponseAndClosesInbox()
    {
        var scope = ExecutionTestScopes.Scope();
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var chat = new RecordingChatClient("only answer");
        var runtime = await CreateRuntimeAsync(chat);

        await RunAsync(scope, runtime, Input("start"));

        Assert.Single(chat.Requests);
        Assert.Equal(SteerOfferResult.Closed, inbox.Offer(Guid.CreateVersion7(), Input("late")));
    }

    [Fact]
    public async Task TurnPipeline_PendingSteerAtTurnEnd_IsReturnedBeforeTurnFinished()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink);
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var steerId = Guid.CreateVersion7();
        inbox.Offer(steerId, Input("never delivered"));
        var envelope = new TurnEnvelope(
            scope.Context.TurnId,
            scope.ProjectConversationId,
            scope.Context.AgentId!.Value,
            AgentRuntimeType.Agent,
            scope.Context.TurnId.ToString("D")
        );

        await TurnPipeline.RunAsync(
            envelope,
            scope,
            Empty(),
            stream: true,
            sink,
            TestContext.Current.CancellationToken
        );

        var returnedIndex = sink.Messages.FindIndex(message => IsType(message, AgwMessageTypes.SteerReturned));
        var finishedIndex = sink.Messages.FindIndex(message => AgwMessageClassifier.IsTurnFinished(message));
        Assert.True(returnedIndex >= 0);
        Assert.True(returnedIndex < finishedIndex);
        var returned = sink.Messages[returnedIndex];
        Assert.Equal(steerId.ToString("D"), returned.AdditionalProperties!["steerId"]);
        Assert.Equal("turn-completed", returned.AdditionalProperties["reason"]);
        Assert.Equal("never delivered", Assert.IsType<AgwTextContent>(Assert.Single(returned.Contents)).Content);
        Assert.Equal(SteerOfferResult.Closed, inbox.Offer(Guid.CreateVersion7(), Input("late")));
    }

    private static async IAsyncEnumerable<AgwMessage> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<AgentRuntime> CreateRuntimeAsync(IChatClient chat)
    {
        var agent = new ChatClientAgent(chat, new ChatClientAgentOptions { Name = "steer-agent" });
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        return new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null
        );
    }

    private static async Task<List<AgwMessage>> RunAsync(
        Agw.Agents.Execution.Context.ExecutionScope scope,
        AgentRuntime runtime,
        AgwUserInput input
    )
    {
        scope.BindInteractions(null, null, null);
        var executor = new AgentTurnExecutor(null, null, null, NullLogger<AgentTurnExecutor>.Instance);
        var messages = new List<AgwMessage>();
        await foreach (
            var message in scope.RunStreaming(
                executor.RunAsync(scope, runtime, new TurnInput(input), TestContext.Current.CancellationToken)
            )
        )
        {
            messages.Add(message);
        }
        return messages;
    }

    private static async Task Drain(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        await foreach (var _ in updates) { }
    }

    private static string ReadText(AgwMessage message) =>
        string.Concat(message.Contents.OfType<AgwTextContent>().Select(content => content.Content));

    private static bool IsType(AgwMessage message, string type) =>
        string.Equals(AgwMessageClassifier.GetMessageType(message), type, StringComparison.Ordinal);

    private static AgwUserInput Input(string text) => new() { Contents = [new AgwTextContent { Content = text }] };

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string[] _responses;

        public RecordingChatClient(params string[] responses)
        {
            _responses = responses;
        }

        public List<List<ChatMessage>> Requests { get; } = [];
        public Action<int>? OnRequest { get; init; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var index = Requests.Count;
            Requests.Add(messages.ToList());
            OnRequest?.Invoke(index);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, _responses[Math.Min(index, _responses.Length - 1)]);
        }
    }
}
