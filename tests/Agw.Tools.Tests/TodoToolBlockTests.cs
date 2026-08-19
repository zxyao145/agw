using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Tools.ToolBlocks.Blocks.Mode;
using Agw.Tools.ToolBlocks.Blocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class TodoToolBlockTests
{
    [Fact]
    public async Task MaterializeAsync_WithoutMode_EvaluatorDoesNotRequireModeProvider()
    {
        var registry = new ToolBlockRegistry([new TodoToolBlock()]);
        await using var contribution = await registry.MaterializeAsync(
            [new TodoToolBlockDefinition()],
            ToolBlockScope.Agent,
            CreateContext(),
            TestContext.Current.CancellationToken
        );
        var agent = new ContextProviderAgent(contribution.ContextProviders);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var evaluator = Assert.Single(contribution.LoopEvaluators);

        Assert.Equal(
            ["todos_add", "todos_complete", "todos_get_all", "todos_get_remaining", "todos_remove"],
            contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal)
        );

        var result = await evaluator.EvaluateAsync(
            CreateLoopContext(agent, session),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.ShouldReinvoke);
        Assert.Null(agent.GetService<AgentModeProvider>());
    }

    [Fact]
    public async Task MaterializeAsync_WithMode_EvaluatorResolvesModeProvider()
    {
        var registry = new ToolBlockRegistry([new TodoToolBlock(), new ModeToolBlock()]);
        await using var contribution = await registry.MaterializeAsync(
            [new TodoToolBlockDefinition(), new ModeToolBlockDefinition()],
            ToolBlockScope.Agent,
            CreateContext(),
            TestContext.Current.CancellationToken
        );
        var agent = new ContextProviderAgent(contribution.ContextProviders);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var evaluator = Assert.Single(contribution.LoopEvaluators);

        var result = await evaluator.EvaluateAsync(
            CreateLoopContext(agent, session),
            TestContext.Current.CancellationToken
        );

        Assert.False(result.ShouldReinvoke);
        Assert.NotNull(agent.GetService<AgentModeProvider>());
    }

    private static ToolMaterializationContext CreateContext() =>
        new()
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = new Project { Id = Guid.CreateVersion7(), Workspace = "/workspace" },
            Workspace = "/workspace",
            DefaultMode = "execute",
        };

    private static LoopContext CreateLoopContext(AIAgent agent, AgentSession session) =>
        new(agent, session, [], new AgentResponse(new List<ChatMessage>()));

    private sealed class ContextProviderAgent : AIAgent
    {
        private readonly IReadOnlyList<AIContextProvider> _providers;

        public ContextProviderAgent(IReadOnlyList<AIContextProvider> providers)
        {
            _providers = providers;
        }

        public override object? GetService(Type serviceType, object? serviceKey = null)
        {
            return base.GetService(serviceType, serviceKey)
                ?? _providers
                    .Select(provider => provider.GetService(serviceType, serviceKey))
                    .FirstOrDefault(service => service != null);
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
    }

    private sealed class TestAgentSession : AgentSession;
}
