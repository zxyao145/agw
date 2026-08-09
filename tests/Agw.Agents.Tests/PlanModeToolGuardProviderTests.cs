using System.Runtime.CompilerServices;
using System.Text.Json;

using Agw.Agents.Execution.Agents.AIContextProviders.PlanMode;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class PlanModeToolGuardProviderTests
{
    [Fact]
    public async Task InvokingAsync_PlanMode_ExposesOnlyExplicitlyAllowedTools()
    {
        var modeProvider = CreateModeProvider("plan");
        var session = new TestAgentSession();
        var guard = new PlanModeToolGuardProvider(
            modeProvider,
            new HashSet<string>(["project_memory_read"], StringComparer.OrdinalIgnoreCase));
        var context = await InvokeAsync(
            guard,
            session,
            [
                CreateFunction("project_memory_read"),
                CreateFunction("run_shell"),
                CreateFunction("connection_read_only", readOnlyHint: true)
            ]);
        var tools = context.Tools!.ToArray();

        Assert.Equal(
            ["connection_read_only", "project_memory_read", "run_shell"],
            tools.Select(static tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["connection_read_only", "run_shell"],
            tools
                .OfType<PlanModeRestrictedAIFunction>()
                .Where(static function => function.HideFromModel)
                .Select(static function => function.Name)
                .Order(StringComparer.Ordinal));
        Assert.Contains("Plan mode is enforced by the server", context.Instructions);
        Assert.Contains("Todo tools may be used", context.Instructions);
        Assert.Contains("external Connection/MCP tools", context.Instructions);
    }

    [Fact]
    public async Task InvokingAsync_PlanMode_DuplicateAllowedNameFailsClosed()
    {
        var guard = new PlanModeToolGuardProvider(
            CreateModeProvider("plan"),
            new HashSet<string>(["file_access_read"], StringComparer.OrdinalIgnoreCase));

        var context = await InvokeAsync(
            guard,
            new TestAgentSession(),
            [CreateFunction("file_access_read"), CreateFunction("file_access_read")]);

        Assert.Empty(context.Tools!);
    }

    [Fact]
    public async Task InvokingAsync_WithoutSession_FailsClosedAsPlanMode()
    {
        var guard = new PlanModeToolGuardProvider(
            CreateModeProvider("execute"),
            new HashSet<string>(["mode_get"], StringComparer.OrdinalIgnoreCase));

        var context = await InvokeAsync(
            guard,
            session: null,
            [CreateFunction("mode_get"), CreateFunction("run_shell")]);
        var tools = context.Tools!.ToArray();

        Assert.Equal(
            ["mode_get", "run_shell"],
            tools.Select(static tool => tool.Name));
        Assert.True(Assert.IsType<PlanModeRestrictedAIFunction>(tools[1]).HideFromModel);
        Assert.Contains(PlanModeToolGuardProvider.EnforcementInstructions, context.Instructions);
    }

    [Fact]
    public async Task InvokingAsync_UnknownMode_FailsClosedAsPlanMode()
    {
        var modeProvider = new AgentModeProvider(
            new AgentModeProviderOptions
            {
                DefaultMode = "review",
                Modes =
                [
                    new AgentModeProviderOptions.AgentMode("review", "Review."),
                    new AgentModeProviderOptions.AgentMode("execute", "Execute.")
                ]
            });
        var guard = new PlanModeToolGuardProvider(
            modeProvider,
            new HashSet<string>(["mode_get"], StringComparer.OrdinalIgnoreCase));

        var context = await InvokeAsync(
            guard,
            new TestAgentSession(),
            [CreateFunction("mode_get"), CreateFunction("run_shell")]);

        var tools = context.Tools!.ToArray();
        Assert.True(Assert.IsType<PlanModeRestrictedAIFunction>(tools[1]).HideFromModel);
        Assert.Contains(PlanModeToolGuardProvider.EnforcementInstructions, context.Instructions);
    }

    [Fact]
    public async Task InvokingAsync_PlanMode_FullAccessCannotAutoApproveRestrictedTool()
    {
        var invocationCount = 0;
        var restricted = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invocationCount++;
                    return "formatted";
                }),
                new AIFunctionFactoryOptions { Name = "run_shell" }));
        var guard = new PlanModeToolGuardProvider(
            CreateModeProvider("plan"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var context = await InvokeAsync(guard, new TestAgentSession(), [restricted]);
        var guardedFunction = Assert.IsType<PlanModeRestrictedAIFunction>(
            Assert.Single(context.Tools!));

        var result = await guardedFunction.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.True(guardedFunction.HideFromModel);
        Assert.Null(guardedFunction.GetService(typeof(ApprovalRequiredAIFunction)));
        Assert.Contains("403_0003 PlanModeToolNotAllowed", Assert.IsType<string>(result));
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task WrappedExecuteTool_AfterSwitchingToPlan_RejectsBeforeInnerInvocation()
    {
        var invocationCount = 0;
        var inner = AIFunctionFactory.Create(
            (Func<string>)(() =>
            {
                invocationCount++;
                return "formatted";
            }),
            new AIFunctionFactoryOptions { Name = "run_shell" });
        var approvalWrapped = new ApprovalRequiredAIFunction(inner);
        var modeProvider = CreateModeProvider("execute");
        var session = new TestAgentSession();
        var guard = new PlanModeToolGuardProvider(
            modeProvider,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var executeContext = await InvokeAsync(guard, session, [approvalWrapped]);
        var guardedFunction = Assert.IsType<PlanModeRestrictedAIFunction>(
            Assert.Single(executeContext.Tools!));

        await modeProvider.SetModeAsync(
            session,
            "plan",
            TestContext.Current.CancellationToken);
        var result = await guardedFunction.InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Contains("403_0003 PlanModeToolNotAllowed", Assert.IsType<string>(result));
        Assert.Contains("run_shell", Assert.IsType<string>(result));
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task InvokingAsync_ExecuteMode_PreservesToolsAndWrapsOnlyRestrictedFunctions()
    {
        var modeProvider = CreateModeProvider("execute");
        var context = await InvokeAsync(
            new PlanModeToolGuardProvider(
                modeProvider,
                new HashSet<string>(["web_search"], StringComparer.OrdinalIgnoreCase)),
            new TestAgentSession(),
            [CreateFunction("web_search"), CreateFunction("run_shell")]);

        Assert.Collection(
            context.Tools!,
            tool => Assert.Equal("web_search", Assert.IsAssignableFrom<AIFunction>(tool).Name),
            tool => Assert.False(Assert.IsType<PlanModeRestrictedAIFunction>(tool).HideFromModel));
        Assert.Null(context.Instructions);
    }

    [Fact]
    public async Task InvokingAsync_ExecuteMode_DuplicateAllowedNameRemainsGuardedAfterPlanSwitch()
    {
        var invocationCount = 0;
        var modeProvider = CreateModeProvider("execute");
        var session = new TestAgentSession();
        var guard = new PlanModeToolGuardProvider(
            modeProvider,
            new HashSet<string>(["file_access_read"], StringComparer.OrdinalIgnoreCase));
        var context = await InvokeAsync(
            guard,
            session,
            [CreateCountingFunction(), CreateCountingFunction()]);
        var functions = context.Tools!.Cast<PlanModeRestrictedAIFunction>().ToArray();

        await modeProvider.SetModeAsync(
            session,
            "plan",
            TestContext.Current.CancellationToken);
        var result = await functions[0].InvokeAsync(
            new AIFunctionArguments(),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, functions.Length);
        Assert.All(functions, static function => Assert.False(function.HideFromModel));
        Assert.Contains("403_0003 PlanModeToolNotAllowed", Assert.IsType<string>(result));
        Assert.Equal(0, invocationCount);

        AIFunction CreateCountingFunction() =>
            AIFunctionFactory.Create(
                (Func<string>)(() =>
                {
                    invocationCount++;
                    return "read";
                }),
                new AIFunctionFactoryOptions { Name = "file_access_read" });
    }

    private static AgentModeProvider CreateModeProvider(string defaultMode) =>
        new(new AgentModeProviderOptions
        {
            DefaultMode = defaultMode,
            Modes =
            [
                new AgentModeProviderOptions.AgentMode("plan", "Plan."),
                new AgentModeProviderOptions.AgentMode("execute", "Execute.")
            ]
        });

    private static AIFunction CreateFunction(string name, bool readOnlyHint = false) =>
        AIFunctionFactory.Create(
            (Func<string>)(() => name),
            new AIFunctionFactoryOptions
            {
                Name = name,
                AdditionalProperties = readOnlyHint
                    ? new AdditionalPropertiesDictionary { ["readOnlyHint"] = true }
                    : null
            });

    private static async Task<AIContext> InvokeAsync(
        PlanModeToolGuardProvider provider,
        AgentSession? session,
        IReadOnlyList<AITool> tools) =>
        await provider.InvokingAsync(
            new AIContextProvider.InvokingContext(
                new TestAgent(),
                session,
                new AIContext { Tools = tools }),
            TestContext.Current.CancellationToken);

    private sealed class TestAgent : AIAgent
    {
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement sessionState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());
    }

    private sealed class TestAgentSession : AgentSession;
}
