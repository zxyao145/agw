using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Jobs.Application.Skills;
using Agw.Shared.Exceptions;
using Agw.Skills.Contracts.Registration;
using Agw.Tools.Abstractions;
using Agw.Tools.Generated;
using Agw.Tools.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Tests;

public sealed class SkillToolCompositionTests
{
    [Fact]
    public async Task AddSkillTools_JobRegistration_BindsDeclaredPermissionsAndSource()
    {
        // Arrange
        using var services = new ServiceCollection().BuildServiceProvider();
        var registration = new JobManagementSkillRegistration();
        var generatedCatalog = new AgwGeneratedToolCatalog(
            services.GetRequiredService<IServiceScopeFactory>(),
            [Agw.Generated.Agw.Jobs.AgwToolModule.Instance]
        );
        await using var capabilities = CreateCapabilities();

        // Act
        capabilities.AddSkillTools(registration, Guid.CreateVersion7(), generatedCatalog);

        // Assert
        Assert.Equal(5, capabilities.Tools.Count);
        Assert.Equal(new[] { "agw_job_get", "agw_job_list" }, capabilities.PlanModeAllowedToolNames.Order());
        foreach (var declaration in generatedCatalog.GetTools(registration.ToolTypes.Single()))
        {
            var tool = Assert.IsAssignableFrom<AIFunction>(
                Assert.Single(capabilities.Tools, tool => tool.Name == declaration.Name)
            );
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(
                new AgwToolMetadata("skill:agw-job", declaration.RequiredPermission, declaration.AllowInPlanMode),
                tool.GetService<AgwToolMetadata>()
            );
            Assert.Equal(
                declaration.RequiredPermission == AgwToolPermission.Write,
                tool.GetService<ApprovalRequiredAIFunction>() != null
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddSkillTools_ConflictingStaticOrDynamicTool_RejectsEntireSkill(bool dynamicTool)
    {
        // Arrange
        var conflictingTool = AIFunctionFactory.Create(() => "existing", "job_write");
        await using var capabilities = CreateCapabilities(
            tools: dynamicTool ? [] : [conflictingTool],
            declaredToolNames: dynamicTool ? new HashSet<string> { "job_write" } : null
        );
        var registration = new TestRegistration([
            new TestTool("job_read", AgwToolPermission.ReadOnly, true, _ => "read"),
            new TestTool("job_write", AgwToolPermission.Write, false, _ => "write"),
        ]);

        // Act
        var error = Assert.Throws<AgwException>(() => capabilities.AddSkillTools(registration, Guid.CreateVersion7()));

        // Assert
        Assert.Equal(ErrorCodes.IntegrationToolNameConflict.Code, error.Code);
        Assert.Equal(dynamicTool ? 0 : 1, capabilities.Tools.Count);
        Assert.Empty(capabilities.PlanModeAllowedToolNames);
    }

    [Fact]
    public async Task AddSkillTools_MismatchedFunctionName_RejectsDeclaration()
    {
        // Arrange
        await using var capabilities = CreateCapabilities();
        var registration = new TestRegistration([
            new TestTool("job_read", AgwToolPermission.ReadOnly, true, _ => "read", "different"),
        ]);

        // Act
        var error = Assert.Throws<AgwException>(() => capabilities.AddSkillTools(registration, Guid.CreateVersion7()));

        // Assert
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
        Assert.Empty(capabilities.Tools);
        Assert.Empty(capabilities.PlanModeAllowedToolNames);
    }

    [Theory]
    [InlineData(AgwToolPermission.ReadOnly, true, 1)]
    [InlineData(AgwToolPermission.Write, false, 0)]
    public async Task RunAsync_PlanMode_EnforcesSkillToolDeclaration(
        AgwToolPermission permission,
        bool allowInPlan,
        int expectedCalls
    )
    {
        // Arrange
        var calls = 0;
        var projectId = Guid.CreateVersion7();
        await using var capabilities = CreateCapabilities(mode: "plan");
        capabilities.AddSkillTools(
            new TestRegistration([
                new TestTool(
                    "job_operation",
                    permission,
                    allowInPlan,
                    id =>
                    {
                        Assert.Equal(projectId, id);
                        calls++;
                        return "ok";
                    }
                ),
            ]),
            projectId
        );
        var client = new CallingChatClient("job_operation");
        using var services = new ServiceCollection().BuildServiceProvider();
        var agent = CreateAgent(client, capabilities, services);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var response = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "Use the job tool")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(allowInPlan, client.ExposedToolNames.Contains("job_operation"));
        Assert.Empty(response.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>());
        if (!allowInPlan)
        {
            var result = Assert.Single(
                response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            );
            Assert.Contains("PlanModeToolNotAllowed", result.Result?.ToString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WriteTool_InvokesOnlyAfterApproval(bool approve)
    {
        // Arrange
        var calls = 0;
        await using var capabilities = CreateCapabilities(mode: "execute");
        capabilities.AddSkillTools(
            new TestRegistration([
                new TestTool(
                    "job_write",
                    AgwToolPermission.Write,
                    false,
                    _ =>
                    {
                        calls++;
                        return "saved";
                    }
                ),
            ]),
            Guid.CreateVersion7()
        );
        var client = new CallingChatClient("job_write");
        using var services = new ServiceCollection().BuildServiceProvider();
        var agent = CreateAgent(client, capabilities, services);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        using var executionScope = ExecutionTestScopes.Scope().Push();

        // Act
        var first = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "Write the job")],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var approval = Assert.Single(
            first.Messages.SelectMany(message => message.Contents).OfType<ToolApprovalRequestContent>()
        );
        Assert.Equal(0, calls);
        await agent.RunAsync(
            [new ChatMessage(ChatRole.User, [approval.CreateResponse(approved: approve)])],
            session,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(approve ? 1 : 0, calls);
    }

    private static AIAgent CreateAgent(
        IChatClient client,
        AgentCapabilityComposition capabilities,
        IServiceProvider services
    ) =>
        client.AsAgwAgent(
            new ResolvedAgentDefinition
            {
                Id = "skill-test",
                Name = "Skill test",
                ModelId = "test",
                OpenTelemetrySourceName = "skill-test",
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            },
            capabilities,
            NullLoggerFactory.Instance,
            services
        );

    private static AgentCapabilityComposition CreateCapabilities(
        IReadOnlyList<AITool>? tools = null,
        IReadOnlySet<string>? declaredToolNames = null,
        string? mode = null
    ) =>
        new(
            tools ?? [],
            [],
            [],
            mode == null ? [] : [new AgentModeProvider(new AgentModeProviderOptions { DefaultMode = mode })],
            [],
            [],
            new HashSet<string>(),
            [],
            new Dictionary<string, string>(),
            new AgentResourceLease(),
            declaredToolNames
        );

    private sealed class TestRegistration : IAgentSkillRegistration
    {
        public TestRegistration(IReadOnlyList<IProjectScopedAgwTool> tools)
        {
            Tools = tools;
        }

        public Guid Id => Guid.Empty;
        public string Name => "test-skill";
        public string Description => "Test skill";
        public IReadOnlyList<IProjectScopedAgwTool> Tools { get; }

        public AgentSkill Create(Guid projectId) => new TestSkill();
    }

    private sealed class TestSkill : AgentClassSkill<TestSkill>
    {
        public override AgentSkillFrontmatter Frontmatter { get; } = new("test-skill", "Test skill");
        protected override string Instructions => "Use the test tool.";
    }

    private sealed class TestTool : IProjectScopedAgwTool
    {
        private readonly Func<Guid, string> _invoke;
        private readonly string? _actualName;

        public TestTool(
            string name,
            AgwToolPermission permission,
            bool allowInPlan,
            Func<Guid, string> invoke,
            string? actualName = null
        )
        {
            Name = name;
            RequiredPermission = permission;
            AllowInPlanMode = allowInPlan;
            _invoke = invoke;
            _actualName = actualName;
        }

        public string Name { get; }
        public AgwToolPermission RequiredPermission { get; }
        public bool AllowInPlanMode { get; }

        public AITool ToAITool(Guid projectId) =>
            AIFunctionFactory.Create(() => _invoke(projectId), _actualName ?? Name);
    }

    private sealed class CallingChatClient : IChatClient
    {
        private readonly string _toolName;

        public CallingChatClient(string toolName)
        {
            _toolName = toolName;
        }

        public HashSet<string> ExposedToolNames { get; } = [];

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(CreateResponse(messages, options));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            foreach (var update in CreateResponse(messages, options).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        private ChatResponse CreateResponse(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            ExposedToolNames.UnionWith(options?.Tools?.Select(tool => tool.Name) ?? []);
            return new ChatResponse(
                messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any()
                    ? new ChatMessage(ChatRole.Assistant, "done")
                    : new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("job-call", _toolName, new Dictionary<string, object?>())]
                    )
            );
        }
    }
}
