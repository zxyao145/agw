using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Tools.Impl.ToolBlocks.Mode;
using Agw.Tools.Impl.ToolBlocks.Todo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class TodoToolBlockTests
{
    [Fact]
    public async Task MaterializeAsync_MemberPermissions_AreDeclaredByToolBlock()
    {
        // Arrange
        var registry = new ToolBlockRegistry([new TodoToolBlock()]);

        // Act
        await using var contribution = await registry.MaterializeAsync(
            [new TodoToolBlockDefinition()],
            ToolBlockScope.Agent,
            CreateContext(),
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(AgwToolPermission.None, contribution.DynamicToolMetadata["todos_add"].RequiredPermission);
        Assert.Equal(AgwToolPermission.ReadOnly, contribution.DynamicToolMetadata["todos_get_all"].RequiredPermission);
        Assert.NotNull(ResolveTodoProvider(contribution.ContextProviders));
    }

    [Fact]
    public async Task MaterializeAsync_LocalProvider_PreservesReferenceTodoBehavior()
    {
        // Arrange
        var registry = new ToolBlockRegistry([new TodoToolBlock()]);
        await using var contribution = await registry.MaterializeAsync(
            [new TodoToolBlockDefinition()],
            ToolBlockScope.Agent,
            CreateContext(),
            TestContext.Current.CancellationToken
        );
        AgwTodoProvider provider = Assert.IsType<AgwTodoProvider>(ResolveTodoProvider(contribution.ContextProviders));
        var agent = new ContextProviderAgent(contribution.ContextProviders);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
#pragma warning disable MAAI001
        var invokingContext = new AIContextProvider.InvokingContext(agent, session, new AIContext());
#pragma warning restore MAAI001
        AIContext context = await provider.InvokingAsync(invokingContext, TestContext.Current.CancellationToken);
        AIFunction addTodos = GetTool(context.Tools!, "todos_add");
        AIFunction completeTodos = GetTool(context.Tools!, "todos_complete");

        // Act
        await addTodos.InvokeAsync(
            new AIFunctionArguments
            {
                ["todos"] = new List<AgwTodoItemInput>
                {
                    new() { Title = "  First  ", Description = "  Details  " },
                    new() { Title = "Second" },
                },
            },
            TestContext.Current.CancellationToken
        );
        await completeTodos.InvokeAsync(
            new AIFunctionArguments
            {
                ["items"] = new List<AgwTodoCompleteInput>
                {
                    new() { Id = 1, Reason = "Done" },
                },
            },
            TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(["AgwTodoProvider"], provider.StateKeys);
        IReadOnlyList<AgwTodoItem> all = await provider.GetAllTodosAsync(
            session,
            TestContext.Current.CancellationToken
        );
        Assert.Collection(
            all,
            item =>
            {
                Assert.Equal(1, item.Id);
                Assert.Equal("First", item.Title);
                Assert.Equal("Details", item.Description);
                Assert.True(item.IsComplete);
            },
            item =>
            {
                Assert.Equal(2, item.Id);
                Assert.Equal("Second", item.Title);
                Assert.Null(item.Description);
                Assert.False(item.IsComplete);
            }
        );
        Assert.Equal(
            "Second",
            Assert.Single(await provider.GetRemainingTodosAsync(session, TestContext.Current.CancellationToken)).Title
        );
    }

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
    public async Task MaterializeAsync_WithRemainingLocalTodo_EvaluatorRequestsReinvoke()
    {
        // Arrange
        var registry = new ToolBlockRegistry([new TodoToolBlock()]);
        await using var contribution = await registry.MaterializeAsync(
            [new TodoToolBlockDefinition()],
            ToolBlockScope.Agent,
            CreateContext(),
            TestContext.Current.CancellationToken
        );
        AgwTodoProvider provider = Assert.IsType<AgwTodoProvider>(ResolveTodoProvider(contribution.ContextProviders));
        var agent = new ContextProviderAgent(contribution.ContextProviders);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
#pragma warning disable MAAI001
        AIContext context = await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(agent, session, new AIContext()),
            TestContext.Current.CancellationToken
        );
#pragma warning restore MAAI001
        await GetTool(context.Tools!, "todos_add")
            .InvokeAsync(
                new AIFunctionArguments { ["todos"] = new List<AgwTodoItemInput> { new() { Title = "Pending work" } } },
                TestContext.Current.CancellationToken
            );

        // Act
        LoopEvaluation result = await Assert
            .Single(contribution.LoopEvaluators)
            .EvaluateAsync(CreateLoopContext(agent, session), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.ShouldReinvoke);
        Assert.Contains("Pending work", result.Feedback);
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

    private static AIFunction GetTool(IEnumerable<AITool> tools, string name) =>
        (AIFunction)tools.Single(tool => tool is AIFunction function && function.Name == name);

    private static AgwTodoProvider? ResolveTodoProvider(IEnumerable<AIContextProvider> providers) =>
        providers
            .Select(static provider => provider.GetService<AgwTodoProvider>())
            .FirstOrDefault(static provider => provider != null);

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
