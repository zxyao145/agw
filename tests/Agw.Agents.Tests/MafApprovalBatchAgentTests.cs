using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

/// <summary>
/// 用真实的 Agent 管线、AgentTurnExecutor 与待处理集合验证审批批次；只有模型调用由脚本代替。
/// Verifies approval batches with the real Agent pipeline, AgentTurnExecutor and pending sets; only the model call is scripted.
/// </summary>
public sealed class MafApprovalBatchAgentTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<string> _executed = [];

    public MafApprovalBatchAgentTests()
    {
        var accessor = new HumanInteractionContextAccessor(new AgentExecutionContextAccessor());
        _services = new ServiceCollection()
            .AddSingleton(accessor)
            .AddSingleton<IHumanInteractionContextAccessor>(accessor)
            .BuildServiceProvider();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_MixedBatchUnderFullAccess_PublishesOnlyInputAndRunsApprovedTool(bool cancelInput)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call == 1 ? [WriteCall("write-1", "a.txt"), InputCall("input-1", "a")] : [new TextContent("done")]
        );
        var sink = new InteractionTestSink();
        var turn = await CreateInProcessTurnAsync(model, AgwPermissionMode.FullAccess, sink, token);
        sink.OnWrite = async (message, cancellationToken) =>
        {
            var request = Assert.IsType<UserInputInteraction>(InteractionTestData.Read(message));
            Assert.NotNull(await turn.Set.TryResolveAsync(Answer(request, cancelInput), cancellationToken));
        };

        // Act
        await turn.RunAsync(token);

        // Assert
        var published = Assert.Single(sink.Messages);
        Assert.Equal("collect_input", InteractionTestData.Read(published).Source.ToolName);
        Assert.Equal(cancelInput ? ["write:a.txt"] : new[] { "write:a.txt", "input:a:A" }, _executed);
        Assert.Equal(2, model.CallCount);
        var snapshot = turn.Set.Snapshot();
        Assert.Equal(1, snapshot.ApprovalRounds);
        Assert.Equal(PendingInteractionStatus.Consumed, Assert.Single(snapshot.Entries).Status);
        Assert.Equal(TurnOutcomeStatus.Completed, turn.Scope.Outcome!.Status);
        Assert.Null(MafApprovalBatchAgent.ReadBatch(turn.Session));
    }

    [Fact]
    public async Task RunAsync_TwoInputsInOneStep_PublishesWholeBatchBeforeAnyAnswer()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call == 1 ? [InputCall("input-1", "a"), InputCall("input-2", "b")] : [new TextContent("done")]
        );
        var sink = new InteractionTestSink();
        var turn = await CreateInProcessTurnAsync(model, AgwPermissionMode.AlwaysAsk, sink, token);
        sink.OnWrite = async (_, cancellationToken) =>
        {
            if (sink.Messages.Count < 2)
                return;
            foreach (var message in sink.Messages)
            {
                var request = Assert.IsType<UserInputInteraction>(InteractionTestData.Read(message));
                Assert.NotNull(await turn.Set.TryResolveAsync(Answer(request, cancelled: false), cancellationToken));
            }
        };

        // Act
        await turn.RunAsync(token);

        // Assert
        Assert.Equal(2, sink.Messages.Count);
        Assert.Equal(["input:a:A", "input:b:B"], _executed);
        Assert.Equal(2, model.CallCount);
        var snapshot = turn.Set.Snapshot();
        Assert.Equal(1, snapshot.ApprovalRounds);
        Assert.Single(snapshot.Entries.Select(entry => entry.BatchId).Distinct());
    }

    [Fact]
    public async Task RunAsync_AutomaticBatchesWithoutEnd_StopsAtIterationLimitWithoutHumanRounds()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call => [WriteCall($"write-{call}", "a.txt")]);
        var sink = new InteractionTestSink();
        var turn = await CreateInProcessTurnAsync(model, AgwPermissionMode.FullAccess, sink, token);

        // Act
        var error = await Assert.ThrowsAsync<AgwException>(() => turn.RunAsync(token));

        // Assert
        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, error.Code);
        Assert.Contains(MafApprovalBatchAgent.MaxAutoApprovalIterations.ToString(), error.Message);
        Assert.Equal(MafApprovalBatchAgent.MaxAutoApprovalIterations + 1, model.CallCount);
        Assert.Equal(MafApprovalBatchAgent.MaxAutoApprovalIterations, _executed.Count);
        Assert.Equal(0, turn.Set.Snapshot().ApprovalRounds);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public async Task RunAsync_AutomaticBatchesThenAnswer_EndsAutomaticSequence()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call <= 3 ? [WriteCall($"write-{call}", $"{call}.txt")] : [new TextContent("done")]
        );
        var turn = await CreateInProcessTurnAsync(
            model,
            AgwPermissionMode.FullAccess,
            new InteractionTestSink(),
            token
        );

        // Act
        await turn.RunAsync(token);

        // Assert
        Assert.Equal(["write:1.txt", "write:2.txt", "write:3.txt"], _executed);
        Assert.Equal(0, MafApprovalBatchAgent.ReadAutoApprovalIterations(turn.Session));
        Assert.Equal(0, turn.Set.Snapshot().ApprovalRounds);
    }

    [Fact]
    public async Task RunAsync_PermissionChangedWhileWaiting_KeepsPendingBatchAndAppliesToNextTurn()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call switch
            {
                1 => [WriteCall("write-1", "first.txt")],
                3 => [WriteCall("write-2", "second.txt")],
                _ => [new TextContent("done")],
            }
        );
        var agent = CreateAgent(model);
        var session = await agent.CreateSessionAsync(token);
        ToolApprovalRequestContent approval;
        using (PushPermission(AgwPermissionMode.AlwaysAsk))
        {
            var response = await agent.RunAsync("write the first file", session, cancellationToken: token);
            approval = Assert.Single(
                response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()
            );
        }

        // Act
        using (PushPermission(AgwPermissionMode.FullAccess))
        {
            var waiting = await agent.RunAsync("any update?", session, cancellationToken: token);
            Assert.Empty(waiting.Messages);
            var pending = Assert.Single(
                Assert.IsType<MafApprovalBatchState>(MafApprovalBatchAgent.ReadBatch(session)).Items
            );
            Assert.True(pending.RequiresHuman);
            Assert.Null(pending.Response);
            Assert.Empty(_executed);

            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, [approval.CreateResponse(approved: true)])],
                session,
                cancellationToken: token
            );
            var nextTurn = await agent.RunAsync("write the second file", session, cancellationToken: token);

            // Assert
            Assert.DoesNotContain(
                nextTurn.Messages.SelectMany(message => message.Contents),
                content => content is ToolApprovalRequestContent
            );
        }
        Assert.Equal(["write:first.txt", "write:second.txt"], _executed);
        Assert.Contains(model.Requests[1], message => message.Text == "any update?");
        Assert.Equal(4, model.CallCount);
    }

    [Fact]
    public async Task RunAsync_AnswerDoesNotMatchOriginalRequest_FailsWithConflict()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call == 1 ? [WriteCall("write-1", "a.txt")] : [new TextContent("done")]
        );
        var agent = CreateAgent(model);
        var session = await agent.CreateSessionAsync(token);
        using var permission = PushPermission(AgwPermissionMode.AlwaysAsk);
        var response = await agent.RunAsync("write", session, cancellationToken: token);
        var approval = Assert.Single(
            response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()
        );

        // Act
        var unknown = await Assert.ThrowsAsync<AgwException>(() =>
            agent.RunAsync(
                [
                    new ChatMessage(
                        ChatRole.User,
                        [new ToolApprovalResponseContent("other", true, new FunctionCallContent("other", "write_file"))]
                    ),
                ],
                session,
                cancellationToken: token
            )
        );
        var changedArguments = await Assert.ThrowsAsync<AgwException>(() =>
            agent.RunAsync(
                [
                    new ChatMessage(
                        ChatRole.User,
                        [
                            new ToolApprovalResponseContent(
                                approval.RequestId,
                                true,
                                new FunctionCallContent(
                                    approval.ToolCall.CallId,
                                    "write_file",
                                    new Dictionary<string, object?> { ["path"] = "other.txt" }
                                )
                            ),
                        ]
                    ),
                ],
                session,
                cancellationToken: token
            )
        );
        var withoutBatch = await Assert.ThrowsAsync<AgwException>(async () =>
            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, [approval.CreateResponse(approved: true)])],
                await agent.CreateSessionAsync(token),
                cancellationToken: token
            )
        );

        // Assert
        Assert.All(
            new[] { unknown, changedArguments, withoutBatch },
            error => Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, error.Code)
        );
        Assert.Empty(_executed);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task RunAsync_TurnCancelledWhileWaiting_StopsWithoutRunningTool()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call == 1 ? [WriteCall("write-1", "a.txt")] : [new TextContent("done")]
        );
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sink = new InteractionTestSink();
        var turn = await CreateInProcessTurnAsync(model, AgwPermissionMode.AlwaysAsk, sink, token);
        sink.OnWrite = (_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.RunAsync(cancellation.Token));

        // Assert
        Assert.Empty(_executed);
        Assert.Equal(PendingInteractionStatus.Pending, Assert.Single(turn.Set.Snapshot().Entries).Status);
        Assert.Equal(1, model.CallCount);
    }

    [Fact]
    public async Task RunAsync_DurableWaitThenResume_ContinuesSameBatchAndRunsToolOnce()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var model = new ScriptedModel(call =>
            call == 1 ? [WriteCall("write-1", "a.txt")] : [new TextContent("done")]
        );
        var agent = CreateAgent(model);
        var first = CreateDurableScope();
        await using var firstRuntime = CreateRuntime(agent, await agent.CreateSessionAsync(token), first);
        await RunTurnAsync(first, firstRuntime, new TurnInput(UserInput("write")), token);
        var outcome = first.Outcome!;
        var request = Assert.IsType<ToolApprovalInteraction>(Assert.Single(outcome.PendingInteractions));
        var restoredSession = await agent.DeserializeSessionAsync(
            await agent.SerializeSessionAsync(firstRuntime.Session, cancellationToken: token),
            cancellationToken: token
        );

        // Act
        var second = CreateDurableScope();
        await using var secondRuntime = CreateRuntime(agent, restoredSession, second);
        await RunTurnAsync(
            second,
            secondRuntime,
            new TurnInput(UserInput("write"))
            {
                Resume = new TurnResume
                {
                    ResolvedInteractions =
                    [
                        new DurableResolvedInteraction(
                            request,
                            new ToolApprovalDecision
                            {
                                InteractionId = request.InteractionId,
                                Approved = true,
                                Scope = ApprovalScope.Once,
                            }
                        ),
                    ],
                },
            },
            token
        );

        // Assert
        Assert.Equal(TurnOutcomeStatus.WaitingForHuman, outcome.Status);
        Assert.Equal(TurnOutcomeStatus.Completed, second.Outcome!.Status);
        Assert.Equal(["write:a.txt"], _executed);
        Assert.Equal(2, model.CallCount);
        Assert.Null(MafApprovalBatchAgent.ReadBatch(restoredSession));
    }

    private async Task<InProcessTurn> CreateInProcessTurnAsync(
        IChatClient model,
        AgwPermissionMode mode,
        InteractionTestSink sink,
        CancellationToken cancellationToken
    )
    {
        var context = ExecutionTestScopes.Context(permissionMode: mode);
        var set = new InMemoryPendingInteractionSet(mode, requestOutput: sink);
        var scope = ExecutionScope.Create(context, Guid.CreateVersion7(), sink, set);
        var registry = new InteractionRequestRegistry();
        scope.BindInteractions(
            new PendingInteractionHandler(HumanInteractionPolicy.Allow, registry),
            new ResolvedHumanInteractionChannel(set),
            registry
        );
        var agent = CreateAgent(model);
        var session = await agent.CreateSessionAsync(cancellationToken);
        return new InProcessTurn(scope, set, CreateRuntime(agent, session, scope));
    }

    private static ExecutionScope CreateDurableScope()
    {
        var context = ExecutionTestScopes.Context(
            permissionMode: AgwPermissionMode.AlwaysAsk,
            provider: ExecutionProvider.Distributed
        );
        var set = new DurablePendingInteractionSet(context.PermissionMode, PendingInteractionSnapshot.Empty);
        var scope = ExecutionScope.Create(context, Guid.CreateVersion7(), new InteractionTestSink(), set);
        var registry = new InteractionRequestRegistry();
        scope.BindInteractions(
            new PendingInteractionHandler(HumanInteractionPolicy.Allow, registry),
            new ResolvedHumanInteractionChannel(set),
            registry
        );
        return scope;
    }

    private static AgentRuntime CreateRuntime(AIAgent agent, AgentSession session, ExecutionScope scope) =>
        new(NullLogger.Instance, agent, session, scope.ProjectId, scope.ContextId, sessionStateScope: null);

    private static async Task RunTurnAsync(
        ExecutionScope scope,
        AgentRuntime runtime,
        TurnInput input,
        CancellationToken cancellationToken
    )
    {
        var executor = new AgentTurnExecutor(null, null, null, NullLogger<AgentTurnExecutor>.Instance);
        await foreach (var _ in scope.RunStreaming(executor.RunAsync(scope, runtime, input, cancellationToken))) { }
    }

    private static IDisposable PushPermission(AgwPermissionMode mode) =>
        ExecutionTestScopes.PushInteractions(
            null,
            new InteractionRequestRegistry(),
            new InteractionPermissionState(mode)
        );

    private AIAgent CreateAgent(IChatClient model)
    {
        var write = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(
                (string path) =>
                {
                    _executed.Add($"write:{path}");
                    return "written";
                },
                new AIFunctionFactoryOptions { Name = "write_file" }
            )
        );
        var input = new HumanInteractionRequiredAIFunction(
            AIFunctionFactory.Create(
                (string caption, string answer) =>
                {
                    _executed.Add($"input:{caption}:{answer}");
                    return answer;
                },
                new AIFunctionFactoryOptions { Name = "collect_input" }
            ),
            new CaptionInputProtocol()
        );
        var capabilities = new AgentCapabilityComposition(
            [write, input],
            [],
            [],
            [new DeferredHumanInteractionProvider()],
            [],
            [],
            new HashSet<string>(),
            [],
            new Dictionary<string, string>(),
            new AgentResourceLease()
        );
        return model.AsAgwAgent(
            new ResolvedAgentDefinition
            {
                Id = "batch-agent",
                Name = "Batch Agent",
                ModelId = "test",
                OpenTelemetrySourceName = "test",
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            },
            capabilities,
            NullLoggerFactory.Instance,
            _services
        );
    }

    private static AgwUserInput UserInput(string text) => new() { Contents = [new AgwTextContent { Content = text }] };

    private static FunctionCallContent WriteCall(string callId, string path) =>
        new(callId, "write_file", new Dictionary<string, object?> { ["path"] = path });

    private static FunctionCallContent InputCall(string callId, string caption) =>
        new(callId, "collect_input", new Dictionary<string, object?> { ["caption"] = caption, ["answer"] = "forged" });

    private static UserInputResponse Answer(UserInputInteraction request, bool cancelled) =>
        new()
        {
            InteractionId = request.InteractionId,
            Cancelled = cancelled,
            ResponseData = cancelled
                ? null
                : JsonSerializer.SerializeToElement(
                    new { answer = request.Payload.GetProperty("caption").GetString()!.ToUpperInvariant() }
                ),
        };

    private sealed record InProcessTurn(ExecutionScope Scope, InMemoryPendingInteractionSet Set, AgentRuntime Runtime)
    {
        public AgentSession Session => Runtime.Session;

        public Task RunAsync(CancellationToken cancellationToken) =>
            RunTurnAsync(Scope, Runtime, new TurnInput(UserInput("run")), cancellationToken);
    }

    private sealed class CaptionInputProtocol : IHumanInteractionProtocol
    {
        public UserInputRequest CreateRequest(AIFunctionArguments arguments) =>
            new(
                "caption",
                "Provide a value",
                JsonSerializer.SerializeToElement(new { caption = arguments["caption"] })
            );

        public AIFunctionArguments BindResponse(AIFunctionArguments arguments, UserInputResponse response) =>
            new(
                new Dictionary<string, object?>(arguments)
                {
                    ["answer"] = response.ResponseData!.Value.GetProperty("answer").GetString(),
                }
            )
            {
                Services = arguments.Services,
            };

        public object CreateCancelledResult(AIFunctionArguments arguments, UserInputResponse response) => "cancelled";
    }

    /// <summary>
    /// 按调用序号返回脚本内容的模型，并记录每次请求。
    /// A model returning scripted contents by call number and recording every request.
    /// </summary>
    private sealed class ScriptedModel : IChatClient
    {
        private readonly Func<int, IList<AIContent>> _script;

        public ScriptedModel(Func<int, IList<AIContent>> script)
        {
            _script = script;
        }

        public int CallCount { get; private set; }

        public List<List<ChatMessage>> Requests { get; } = [];

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToList());
            CallCount++;
            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(ChatRole.Assistant, _script(CallCount))
                    {
                        MessageId = Guid.CreateVersion7().ToString("N"),
                    }
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
    }
}
