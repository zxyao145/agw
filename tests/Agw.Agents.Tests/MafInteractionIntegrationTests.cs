using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Durable.Contracts;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.HumanInteraction.InProcess;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class MafInteractionIntegrationTests : IDisposable
{
    private readonly IDisposable _owner = UserInfoUtil.Push(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], "Test"))
    );

    public void Dispose() => _owner.Dispose();

    [Fact]
    public async Task InProcess_CustomProtocol_PublishesAndBindsEveryInput()
    {
        var accessor = new HumanInteractionContextAccessor(new AgentExecutionContextAccessor());
        var completed = new List<string>();
        await using var services = new ServiceCollection()
            .AddSingleton(accessor)
            .AddSingleton<IHumanInteractionContextAccessor>(accessor)
            .BuildServiceProvider();
        using var model = new InputModel();
        var agent = CreateAgent(model, services, completed, deferred: false);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var sink = new InteractionTestSink();
        var interactions = new InProcessInteractionSession(sink, AgwPermissionMode.FullAccess);
        sink.OnWrite = async (message, token) =>
        {
            var request = Assert.IsType<UserInputInteraction>(InteractionTestData.Read(message));
            await interactions.TrySubmitAsync(Answer(request, cancelled: false), token);
        };
        using var scope = ExecutionTestScopes.PushInteractions(
            interactions,
            interactions.Requests,
            interactions.PermissionState
        );
        await agent.RunAsync("run", session, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["a:A:keep", "b:B:keep"], completed);
        Assert.Equal(2, sink.Messages.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_BatchedCustomInputs_RestoreDescriptionsAnswersAndCancellation(bool cancelSecond)
    {
        var token = TestContext.Current.CancellationToken;
        var accessor = new HumanInteractionContextAccessor(new AgentExecutionContextAccessor());
        var completed = new List<string>();
        await using var services = new ServiceCollection()
            .AddSingleton(accessor)
            .AddSingleton<IHumanInteractionContextAccessor>(accessor)
            .BuildServiceProvider();
        using var model = new InputModel();
        var agent = CreateAgent(model, services, completed, deferred: true);
        var session = await agent.CreateSessionAsync(token);
        var registry = new InteractionRequestRegistry();
        var permissions = new InteractionPermissionState(AgwPermissionMode.FullAccess);
        List<ToolApprovalRequestContent> approvals;
        using (ExecutionTestScopes.PushInteractions(ExecutionTestScopes.ResolvedChannel([]), registry, permissions))
        {
            var response = await agent.RunAsync("run", session, cancellationToken: token);
            approvals = response
                .Messages.SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>()
                .ToList();
        }

        // 两个输入作为同一个批次交出；FullAccess 不回答用户输入。
        // Both inputs leave as one batch; FullAccess never answers user input.
        Assert.Equal(["ficc_call-a", "ficc_call-b"], approvals.Select(approval => approval.RequestId));
        Assert.Empty(completed);
        Assert.Equal(2, registry.Snapshot().Count);
        var first = Assert.IsType<UserInputInteraction>(
            MafApprovalAdapter.CreateRequest(approvals[0], "standalone", registry: registry)
        );
        var second = Assert.IsType<UserInputInteraction>(
            MafApprovalAdapter.CreateRequest(approvals[1], "standalone", registry: registry)
        );
        Assert.NotEqual(first.InteractionId, second.InteractionId);
        Assert.Equal("keep", first.Arguments!.Value.GetProperty("marker").GetString());
        var firstAnswer = Answer(first, cancelled: false);
        var state = RoundTrip(new DurableInteractionState { InputCatalog = registry.Snapshot() });
        session = await RoundTripAsync(agent, session, token);

        // 第一个回答只保存在批次中，不调用模型也不执行工具。
        // The first answer is only saved in the batch; neither the model nor a tool runs.
        using (
            ExecutionTestScopes.PushInteractions(
                ExecutionTestScopes.ResolvedChannel([new(first, firstAnswer)]),
                new InteractionRequestRegistry(state.InputCatalog),
                permissions
            )
        )
        {
            var partial = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, [MafApprovalAdapter.CreateResponse(approvals[0], firstAnswer)])],
                session,
                cancellationToken: token
            );
            Assert.Empty(partial.Messages);
        }
        Assert.Empty(completed);
        Assert.Equal(1, model.CallCount);
        session = await RoundTripAsync(agent, session, token);
        var batch = Assert.IsType<MafApprovalBatchState>(MafApprovalBatchAgent.ReadBatch(session));
        Assert.Equal(InteractionIdentity.ForBatch("standalone", ["ficc_call-a", "ficc_call-b"]), batch.BatchId);
        Assert.All(batch.Items, item => Assert.True(item.RequiresHuman));
        Assert.NotNull(batch.Items[0].Response!.AdditionalProperties![MafApprovalAdapter.UserInputResponseProperty]);
        Assert.Null(batch.Items[1].Response);
        Assert.NotNull(HumanInteractionToolMetadata.Read(batch.Items[1].Request));

        // 第二个回答使批次齐全，两个输入工具读取各自的结构化回答。
        // The second answer completes the batch and each input tool reads its own structured answer.
        var secondAnswer = Answer(second, cancelSecond);
        state = RoundTrip(state with { ResolvedInputs = [new(first, firstAnswer), new(second, secondAnswer)] });
        using (
            ExecutionTestScopes.PushInteractions(
                ExecutionTestScopes.ResolvedChannel(state.ResolvedInputs),
                new InteractionRequestRegistry(state.InputCatalog),
                permissions
            )
        )
            await agent.RunAsync(
                [new ChatMessage(ChatRole.User, [MafApprovalAdapter.CreateResponse(approvals[1], secondAnswer)])],
                session,
                cancellationToken: token
            );
        Assert.Equal(cancelSecond ? ["a:A:keep"] : new[] { "a:A:keep", "b:B:keep" }, completed);
        Assert.Equal(2, model.CallCount);
        Assert.Null(MafApprovalBatchAgent.ReadBatch(session));
    }

    private static async Task<AgentSession> RoundTripAsync(
        AIAgent agent,
        AgentSession session,
        CancellationToken cancellationToken
    ) =>
        await agent.DeserializeSessionAsync(
            await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken),
            cancellationToken: cancellationToken
        );

    internal static AIAgent CreateAgent(
        IChatClient model,
        IServiceProvider services,
        List<string> completed,
        bool deferred
    )
    {
        var tool = new HumanInteractionRequiredAIFunction(
            AIFunctionFactory.Create(
                (string caption, string marker, string answer) =>
                {
                    var result = $"{caption}:{answer}:{marker}";
                    completed.Add(result);
                    return result;
                },
                new AIFunctionFactoryOptions { Name = "collect_input" }
            ),
            new SampleInputProtocol()
        );
        var capabilities = new AgentCapabilityComposition(
            [tool],
            [],
            [],
            deferred ? [new DeferredHumanInteractionProvider()] : [],
            [],
            [ToolApprovalAgent.AllToolsAutoApprovalRule],
            new HashSet<string>(),
            [],
            new Dictionary<string, string>(),
            new AgentResourceLease()
        );
        return model.AsAgwAgent(
            new ResolvedAgentDefinition
            {
                Id = "input-agent",
                Name = "Input Agent",
                ModelId = "test",
                OpenTelemetrySourceName = "test",
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            },
            capabilities,
            NullLoggerFactory.Instance,
            services
        );
    }

    internal static UserInputResponse Answer(UserInputInteraction request, bool cancelled) =>
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

    private static DurableInteractionState RoundTrip(DurableInteractionState state) =>
        DurableExecutionJson.DeserializeRequired<DurableInteractionState>(
            DurableExecutionJson.Serialize(state),
            "interaction test"
        );

    private sealed class SampleInputProtocol : IHumanInteractionProtocol
    {
        public UserInputRequest CreateRequest(AIFunctionArguments arguments) =>
            new("sample", "Provide a value", JsonSerializer.SerializeToElement(new { caption = arguments["caption"] }));

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

    internal sealed class InputModel : IChatClient
    {
        public int CallCount { get; private set; }

        public void Dispose() { }

        public object? GetService(Type type, object? key = null) => type.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            return Task.FromResult(
                new ChatResponse([
                    new ChatMessage(
                        ChatRole.Assistant,
                        messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any())
                            ? [new TextContent("done")]
                            : new AIContent[] { Call("a"), Call("b") }
                    )
                    {
                        MessageId = Guid.CreateVersion7().ToString("N"),
                    },
                ])
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

        private static FunctionCallContent Call(string name) =>
            new(
                $"call-{name}",
                "collect_input",
                new Dictionary<string, object?>
                {
                    ["caption"] = name,
                    ["marker"] = "keep",
                    ["answer"] = "forged",
                }
            );
    }
}
