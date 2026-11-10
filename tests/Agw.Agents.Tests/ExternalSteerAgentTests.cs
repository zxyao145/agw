using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Codex;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Steering;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Tests;

public class ExternalSteerAgentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TurnExecutor_EngineAcceptsSteerMidRun_WritesUserMessageAndDeliveredInPlace()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink, context: ExternalContext());
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var engine = new ScriptedEngine();
        var target = new ScriptedTarget(ExternalSteerResult.Delivered) { OnSteer = engine.Release };
        var runtime = await CreateRuntimeAsync(engine, target);
        var steerId = Guid.CreateVersion7();
        engine.OnFirstOutput = () => inbox.Offer(steerId, Input("use the new API"));

        var output = await RunAsync(scope, runtime, Input("start"));

        Assert.Equal(1, engine.Runs);
        Assert.Equal(["before", "after"], output.Where(m => m.Role == AiRole.Assistant).Select(ReadText));
        var userIndex = output.FindIndex(m => m.Role == AiRole.User);
        var deliveredIndex = output.FindIndex(m => IsType(m, AgwMessageTypes.SteerDelivered));
        var beforeIndex = output.FindIndex(m => ReadText(m) == "before");
        Assert.Equal(steerId.ToString("D"), output[userIndex].MessageId);
        Assert.Equal(scope.Context.TurnId.ToString("D"), output[userIndex].AdditionalProperties!["turnId"]);
        Assert.True(beforeIndex < userIndex && userIndex < deliveredIndex);
        Assert.Equal(steerId, Assert.Single(target.Steered).SteerId);
        Assert.Empty(inbox.TakePending());
    }

    [Fact]
    public async Task Agent_EngineAcceptsSteer_PlacesTheUpdateBetweenEngineOutputs()
    {
        var scope = ExecutionTestScopes.Scope(context: ExternalContext());
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var engine = new ScriptedEngine();
        var agent = new ExternalSteerAgent(
            engine,
            _ => new ScriptedTarget(ExternalSteerResult.Delivered),
            NullLogger.Instance
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var steerId = Guid.CreateVersion7();
        var order = new List<string>();

        await foreach (
            var update in scope.RunStreaming(
                agent.RunStreamingAsync(
                    [new ChatMessage(ChatRole.User, "start")],
                    session,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
        )
        {
            if (SteerDelivery.GetDeliveredEntry(update) is { } entry)
            {
                order.Add($"steer:{entry.SteerId:D}");
                Assert.Equal(ChatRole.User, update.Role);
                Assert.Equal("change course", update.Text);
                engine.Release();
                continue;
            }
            order.Add(update.Text);
            if (update.Text == "before")
                inbox.Offer(steerId, Input("change course"));
        }

        Assert.Equal(["before", $"steer:{steerId:D}", "after"], order);
    }

    [Fact]
    public async Task TurnExecutor_EngineRefusesSteer_DeliversItAsTheNextRunOfTheSameTurn()
    {
        var sink = new InteractionTestSink();
        var scope = ExecutionTestScopes.Scope(output: sink, context: ExternalContext());
        var inbox = new SteerInbox(8);
        scope.BindSteers(inbox);
        var engine = new ScriptedEngine();
        var target = new ScriptedTarget(ExternalSteerResult.Retry) { OnSteer = engine.Release };
        var runtime = await CreateRuntimeAsync(engine, target);
        var steerId = Guid.CreateVersion7();
        engine.OnFirstOutput = () => inbox.Offer(steerId, Input("after you finish"));

        var output = await RunAsync(scope, runtime, Input("start"));

        Assert.Equal(2, engine.Runs);
        Assert.Contains(
            engine.Requests[1],
            message => message.Role == ChatRole.User && message.Text == "after you finish"
        );
        Assert.Single(sink.Messages, m => IsType(m, AgwMessageTypes.SteerDelivered));
        Assert.DoesNotContain(output.Concat(sink.Messages), m => IsType(m, AgwMessageTypes.SteerReturned));
        Assert.Equal(["before", "after", "again"], output.Where(m => m.Role == AiRole.Assistant).Select(ReadText));
        Assert.Equal(SteerOfferResult.Closed, inbox.Offer(Guid.CreateVersion7(), Input("late")));
    }

    [Fact]
    public async Task Agent_WithoutSteerInbox_PassesTheEngineStreamThrough()
    {
        var engine = new ScriptedEngine { HoldsAfterFirstOutput = false };
        var agent = new ExternalSteerAgent(
            engine,
            _ => new ScriptedTarget(ExternalSteerResult.Delivered),
            NullLogger.Instance
        );
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var updates = new List<AgentResponseUpdate>();
        await foreach (
            var update in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "start")],
                session,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            updates.Add(update);

        Assert.Equal(["before", "after"], updates.Select(update => update.Text));
    }

    [Fact]
    public async Task CodexTarget_WaitsForATurnAndStopsAfterTurnCompleted()
    {
        var target = new CodexSteerTarget(await CreateUnstartedClientAsync(), NullLogger.Instance);
        var ready = target.WhenReadyAsync(TestContext.Current.CancellationToken);
        Assert.False(ready.IsCompleted);

        target.Observe(Notification("item/agentMessage/delta", "thread-1", "turn-1"));
        await ready.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        target.Observe(Notification("turn/completed", "thread-1", "turn-1"));

        Assert.False(target.WhenReadyAsync(TestContext.Current.CancellationToken).IsCompleted);
        target.Observe(Notification("turn/started", "thread-1", "turn-2"));
        Assert.True(target.WhenReadyAsync(TestContext.Current.CancellationToken).IsCompleted);
    }

    [Fact]
    public async Task CodexTarget_BeforeAnyTurn_RetriesWithoutCallingCodex()
    {
        var target = new CodexSteerTarget(await CreateUnstartedClientAsync(), NullLogger.Instance);

        var result = await target.SteerAsync(
            new SteerEntry(Guid.CreateVersion7(), Input("early")),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ExternalSteerResult.Retry, result);
    }

    [Fact]
    public void CodexInput_TextAndImage_MapToCodexInputItems()
    {
        var entry = new SteerEntry(
            Guid.CreateVersion7(),
            new AgwUserInput
            {
                Contents =
                [
                    new AgwTextContent { Content = "look" },
                    new AgwDataContent("data:image/png;base64,aGk=", "image/png"),
                ],
            }
        );

        var input = CodexSteerTarget.ToCodexInput(entry);

        Assert.Equal("look", Assert.IsType<TextUserInput>(input[0]).Text);
        Assert.Equal("data:image/png;base64,aGk=", Assert.IsType<ImageUserInput>(input[1]).Url.Value);
    }

    [Fact]
    public void Inbox_RequeueAfterClose_FailsFast()
    {
        var inbox = new SteerInbox(8);
        inbox.Close();

        var exception = Assert.Throws<AgwException>(() =>
            inbox.Requeue([new SteerEntry(Guid.CreateVersion7(), Input("late"))])
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
    }

    [Fact]
    public async Task Inbox_WaitForPending_WakesOnOfferAndEndsOnClose()
    {
        var inbox = new SteerInbox(8);
        var waiting = inbox.WaitForPendingAsync(TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        inbox.Offer(Guid.CreateVersion7(), Input("one"));
        Assert.True(await waiting.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        inbox.TakePending();
        var closing = inbox.WaitForPendingAsync(TestContext.Current.CancellationToken);
        inbox.Close();

        Assert.False(await closing.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    private static AgentExecutionContext ExternalContext() => ExecutionTestScopes.Context(engineKind: EngineKind.Codex);

    private static async Task<AgentRuntime> CreateRuntimeAsync(ScriptedEngine engine, ScriptedTarget target)
    {
        var agent = new ExternalSteerAgent(engine, _ => target, NullLogger.Instance);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        return new AgentRuntime(
            NullLogger.Instance,
            agent,
            session,
            Guid.CreateVersion7(),
            "context-1",
            sessionStateScope: null,
            agentType: AgentType.External
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
            messages.Add(message);
        return messages;
    }

    /// <summary>
    /// 不启动进程的 app-server 客户端：只用来构造目标，测试中的路径不会向它发送请求。
    /// An app-server client without a started process: it only constructs the target, and the tested paths never send it a request.
    /// </summary>
    private static Task<CodexAppServerClient> CreateUnstartedClientAsync() =>
        Task.FromResult((CodexAppServerClient)RuntimeHelpers.GetUninitializedObject(typeof(CodexAppServerClient)));

    private static AgentResponseUpdate Notification(string method, string threadId, string turnId) =>
        new()
        {
            Role = ChatRole.Assistant,
            RawRepresentation = new AppServerNotification(
                method,
                JsonDocument.Parse("{}").RootElement,
                threadId,
                turnId
            ),
        };

    private static AgwUserInput Input(string text) => new() { Contents = [new AgwTextContent { Content = text }] };

    private static string ReadText(AgwMessage message) =>
        string.Concat(message.Contents.OfType<AgwTextContent>().Select(content => content.Content));

    private static bool IsType(AgwMessage message, string type) =>
        string.Equals(AgwMessageClassifier.GetMessageType(message), type, StringComparison.Ordinal);

    /// <summary>
    /// 按脚本输出的外部引擎：第一次运行输出 before，等待放行后输出 after；之后的运行直接回答。
    /// An external engine following a script: the first run outputs before, waits for release, then outputs after; later runs answer at once.
    /// </summary>
    private sealed class ScriptedEngine : AIAgent
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs { get; private set; }
        public List<List<ChatMessage>> Requests { get; } = [];
        public Action? OnFirstOutput { get; set; }
        public bool HoldsAfterFirstOutput { get; init; } = true;

        public void Release() => _release.TrySetResult();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new ScriptedSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult<AgentSession>(new ScriptedSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            Runs++;
            Requests.Add(messages.ToList());
            if (Runs > 1)
            {
                yield return Text("again");
                yield break;
            }
            yield return Text("before");
            OnFirstOutput?.Invoke();
            if (HoldsAfterFirstOutput)
                await _release.Task.WaitAsync(cancellationToken);
            yield return Text("after");
        }

        private static AgentResponseUpdate Text(string text) =>
            new(ChatRole.Assistant, text) { MessageId = Guid.CreateVersion7().ToString("D") };

        private sealed class ScriptedSession : AgentSession;
    }

    /// <summary>
    /// 按脚本回答的接收方：第一段输出之后可以接收，每次接收给出固定结果并通知引擎继续。
    /// A receiver answering by script: it can receive after the first output, gives a fixed result each time and lets the engine continue.
    /// </summary>
    private sealed class ScriptedTarget(ExternalSteerResult result) : IExternalSteerTarget
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _refused;

        public List<SteerEntry> Steered { get; } = [];
        public Action? OnSteer { get; init; }

        public void Observe(AgentResponseUpdate update) => _ready.TrySetResult();

        public Task WhenReadyAsync(CancellationToken cancellationToken) =>
            (_refused ? _never.Task : _ready.Task).WaitAsync(cancellationToken);

        public Task<ExternalSteerResult> SteerAsync(SteerEntry entry, CancellationToken cancellationToken)
        {
            if (result == ExternalSteerResult.Delivered)
                Steered.Add(entry);
            else
                _refused = true;
            OnSteer?.Invoke();
            return Task.FromResult(result);
        }
    }
}
