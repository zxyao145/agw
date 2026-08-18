using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;
using Agw.Tools.HumanInteraction;
using Agw.Tools.ToolBlocks.Blocks.Mode;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public sealed class ModeToolBlockTests
{
    [Fact]
    public async Task MaterializeAsync_ModeSetRequiresHumanConfirmationAndModeGetDoesNot()
    {
        var materialized = await MaterializeAsync();
        await using var contribution = materialized.Contribution;

        Assert.IsType<HumanInteractionRequiredAIFunction>(materialized.ModeSet);
        Assert.IsNotType<HumanInteractionRequiredAIFunction>(materialized.ModeGet);
        Assert.Equal(
            "plan",
            ReadStringResult(
                await materialized.ModeGet.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken)
            )
        );
    }

    [Fact]
    public async Task MaterializeAsync_PlanModeUsesRestrictedInstructionsAndMarksControlToolsAllowed()
    {
        var materialized = await MaterializeAsync();
        await using var contribution = materialized.Contribution;

        Assert.Equal(["mode_get", "mode_set"], contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal));
        Assert.Contains("prepare a decision-complete plan", materialized.Context.Instructions);
        Assert.Contains("Do not execute shell commands", materialized.Context.Instructions);
        Assert.Contains("Todo tools may be used", materialized.Context.Instructions);
        Assert.Contains("<proposed_plan>", materialized.Context.Instructions);
        Assert.Contains("</proposed_plan>", materialized.Context.Instructions);
        Assert.Contains("Do not use them for clarifying questions", materialized.Context.Instructions);
        Assert.Contains("do not write any preamble or closing text", materialized.Context.Instructions);
        Assert.DoesNotContain("Create a todo list", materialized.Context.Instructions);
        Assert.DoesNotContain("write the plan", materialized.Context.Instructions);
    }

    [Fact]
    public async Task ModeSet_BeforeHumanConfirmation_RemainsPendingThenChangesMode()
    {
        var materialized = await MaterializeAsync();
        await using var contribution = materialized.Contribution;
        var channel = new TestHumanInteractionChannel();
        await using var services = CreateServices(channel);
        var arguments = CreateArguments("execute", services);

        var pendingResult = materialized.ModeSet.InvokeAsync(arguments, TestContext.Current.CancellationToken).AsTask();
        var request = await channel.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(pendingResult.IsCompleted);
        Assert.Equal(ModeSetHumanInteractionProvider.InteractionKind, request.InteractionKind);
        Assert.Equal("mode_set", request.ToolName);
        Assert.Equal("execute", request.Payload.GetProperty("mode").GetString());

        channel.Submit(
            new HumanInteractionResponse(
                request.RequestId,
                Cancelled: false,
                JsonSerializer.SerializeToElement(new { confirmed = true })
            )
        );

        Assert.Equal("Mode changed to \"execute\".", ReadStringResult(await pendingResult));
        Assert.Equal(
            "execute",
            await materialized.ModeProvider.GetModeAsync(materialized.Session, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ModeSet_WhenHumanCancels_KeepsCurrentMode()
    {
        var materialized = await MaterializeAsync();
        await using var contribution = materialized.Contribution;
        var channel = new TestHumanInteractionChannel();
        await using var services = CreateServices(channel);
        var pendingResult = materialized
            .ModeSet.InvokeAsync(CreateArguments("execute", services), TestContext.Current.CancellationToken)
            .AsTask();
        var request = await channel.RequestReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

        channel.Submit(new HumanInteractionResponse(request.RequestId, Cancelled: true, ResponseData: null));

        Assert.Equal(
            "Mode change to \"execute\" was cancelled by the user.",
            Assert.IsType<string>(await pendingResult)
        );
        Assert.Equal(
            "plan",
            await materialized.ModeProvider.GetModeAsync(materialized.Session, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ModeSet_WithoutInteractiveChannel_FailsWithoutChangingMode()
    {
        var materialized = await MaterializeAsync();
        await using var contribution = materialized.Contribution;
        await using var services = new ServiceCollection().BuildServiceProvider();

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await materialized.ModeSet.InvokeAsync(
                CreateArguments("execute", services),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.AgentExecutionFailed.Code, exception.Code);
        Assert.Contains("requires an active interactive channel", exception.Message);
        Assert.Equal(
            "plan",
            await materialized.ModeProvider.GetModeAsync(materialized.Session, TestContext.Current.CancellationToken)
        );
    }

    private static async Task<MaterializedModeTools> MaterializeAsync()
    {
        var contribution = await new ModeToolBlock().MaterializeAsync(
            new ModeToolBlockDefinition(),
            new ToolMaterializationContext
            {
                Agent = new Agent { Id = Guid.CreateVersion7() },
                Project = new Project { Id = Guid.CreateVersion7(), Workspace = "/workspace" },
                Workspace = "/workspace",
                DefaultMode = "plan",
            },
            TestContext.Current.CancellationToken
        );
        var modeProvider = Assert.IsType<AgentModeProvider>(contribution.ContextProviders[0]);
        var agent = new TestAgent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var aiContext = new AIContext();
        foreach (var provider in contribution.ContextProviders)
        {
            aiContext = await provider.InvokingAsync(
                new AIContextProvider.InvokingContext(agent, session, aiContext),
                TestContext.Current.CancellationToken
            );
        }

        var functions = aiContext
            .Tools!.OfType<AIFunction>()
            .ToDictionary(static function => function.Name, StringComparer.OrdinalIgnoreCase);
        return new MaterializedModeTools(
            contribution,
            modeProvider,
            Assert.IsAssignableFrom<AIFunction>(functions["mode_set"]),
            Assert.IsAssignableFrom<AIFunction>(functions["mode_get"]),
            session,
            aiContext
        );
    }

    private static ServiceProvider CreateServices(IHumanInteractionChannel channel) =>
        new ServiceCollection()
            .AddSingleton<IHumanInteractionContextAccessor>(new TestContextAccessor(channel))
            .BuildServiceProvider();

    private static AIFunctionArguments CreateArguments(string mode, IServiceProvider services) =>
        new(new Dictionary<string, object?> { ["mode"] = mode }) { Services = services };

    private static string? ReadStringResult(object? result) =>
        result switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };

    private sealed record MaterializedModeTools(
        ToolContribution Contribution,
        AgentModeProvider ModeProvider,
        AIFunction ModeSet,
        AIFunction ModeGet,
        AgentSession Session,
        AIContext Context
    );

    private sealed class TestContextAccessor : IHumanInteractionContextAccessor
    {
        public TestContextAccessor(IHumanInteractionChannel current)
        {
            Current = current;
        }

        public IHumanInteractionChannel? Current { get; }
    }

    private sealed class TestHumanInteractionChannel : IHumanInteractionChannel
    {
        private readonly TaskCompletionSource<HumanInteractionResponse> _response = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource<HumanInteractionRequest> RequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<HumanInteractionResponse> RequestAsync(
            HumanInteractionRequest request,
            CancellationToken cancellationToken
        )
        {
            RequestReceived.TrySetResult(request);
            return await _response.Task.WaitAsync(cancellationToken);
        }

        public void Submit(HumanInteractionResponse response) => _response.TrySetResult(response);
    }

    private sealed class TestAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            yield break;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult<AgentSession>(new TestAgentSession());
    }

    private sealed class TestAgentSession : AgentSession;
}
