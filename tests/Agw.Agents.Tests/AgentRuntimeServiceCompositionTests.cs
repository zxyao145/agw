using System.Reflection;
using Agw.Agents.Execution.Agents;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Utils;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;
using ClaudeCodeSdk.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceCompositionTests
{
    [Fact]
    public void BuildInstructions_WhenPromptProvided_ReturnsPrompt()
    {
        var instructions = AgentRuntimeServiceUtil.BuildInstructions("System prompt");

        Assert.Equal("System prompt", instructions);
    }

    [Fact]
    public void BuildInstructions_WhenPromptMissing_UsesDefaultPrompt()
    {
        var instructions = AgentRuntimeServiceUtil.BuildInstructions("   ");

        Assert.Equal("You are a helpful agent.", instructions);
    }

    [Fact]
    public void MergeEnvironmentVariables_ExecutionValuesOverrideAgentDefinitionValues()
    {
        var agentVariables = new Dictionary<string, string> { ["SHARED"] = "agent", ["AGENT_ONLY"] = "agent" };
        var executionVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "session",
            ["SESSION_ONLY"] = "session",
        };

        var result = AgentRuntimeServiceUtil.MergeEnvironmentVariables(agentVariables, executionVariables);

        Assert.Equal("session", result["SHARED"]);
        Assert.Equal("agent", result["AGENT_ONLY"]);
        Assert.Equal("session", result["SESSION_ONLY"]);
    }

    [Fact]
    public void MergeEnvironmentVariables_ProjectAndExecutionValuesOverrideEarlierLayers()
    {
        var agentVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "agent",
            ["PROJECT_SHARED"] = "agent",
            ["AGENT_ONLY"] = "agent",
        };
        var projectVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "project",
            ["PROJECT_SHARED"] = "project",
            ["PROJECT_ONLY"] = "project",
        };
        var executionVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "session",
            ["SESSION_ONLY"] = "session",
        };

        var result = AgentRuntimeServiceUtil.MergeEnvironmentVariables(
            agentVariables,
            projectVariables,
            executionVariables
        );

        Assert.Equal("session", result["SHARED"]);
        Assert.Equal("project", result["PROJECT_SHARED"]);
        Assert.Equal("agent", result["AGENT_ONLY"]);
        Assert.Equal("project", result["PROJECT_ONLY"]);
        Assert.Equal("session", result["SESSION_ONLY"]);
    }

    [Fact]
    public void ExternalAgentNames_Codex_HasDefaultCodexOptions()
    {
        var codexAgent = Assert.Single(AgentNames.ExternalAgentNames, agent => agent.Name == AgentNames.Codex);

        Assert.False(string.IsNullOrWhiteSpace(codexAgent.Extra));
        var options = JsonUtil.Deserialize<CodexAIAgentOptions>(codexAgent.Extra!);

        Assert.NotNull(options);
        Assert.NotNull(options.CodexOptions);
        Assert.NotNull(options.ThreadOptions);
    }

    [Theory]
    [InlineData(AgentNames.Codex)]
    [InlineData(AgentNames.ClaudeCode)]
    public async Task TryCreateExternalAgent_WrapsSdkAgentAndDisablesSdkHistoryProvider(string agentName)
    {
        var historyProvider = new InMemoryChatHistoryProvider();
        var service = new AgentRuntimeService(
            agentAppService: null!,
            projectAppService: null!,
            capabilityComposer: null!,
            historyProvider,
            providerSessionState: null!,
            taskSessionBindingService: null!,
            dataPaths: null!,
            fileSystemResolver: null!,
            sessionStateStore: null!,
            NullLogger<AgentRuntimeService>.Instance,
            new ObservabilityMiddleware(NullLogger<ObservabilityMiddleware>.Instance),
            new UsageTrackingMiddleware(
                providerSessionState: null!,
                usageRecorder: null!,
                NullLogger<UsageTrackingMiddleware>.Instance
            ),
            summaryService: null!,
            timeProvider: TimeProvider.System
        );
        var method = typeof(AgentRuntimeService).GetMethod(
            "TryCreateExternalAgent",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var request = new CreateAiAgentRequest
        {
            Agent = new Agent { Name = agentName, Type = AgentType.External },
        };
        object?[] arguments =
        [
            request,
            new Project { Workspace = Path.GetTempPath(), ExtraSetting = "{}" },
            new Dictionary<string, string>(),
            null,
            false,
        ];

        Assert.NotNull(method);
        Assert.True(Assert.IsType<bool>(method.Invoke(service, arguments)));
        var agent = Assert.IsAssignableFrom<AIAgent>(arguments[3]);
        Assert.NotNull(agent.GetService<ExternalAgentChatHistoryAgent>());

        if (agentName == AgentNames.Codex)
        {
            Assert.Null(Assert.IsType<CodexAIAgent>(agent.GetService<CodexAIAgent>()).ChatHistoryProvider);
        }
        else
        {
            var claudeAgent = Assert.IsType<ClaudeCodeAIAgent>(agent.GetService<ClaudeCodeAIAgent>());
            Assert.Null(claudeAgent.ChatHistoryProvider);
            await claudeAgent.DisposeAsync();
        }
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenWorkspaceProvided_SetsThreadWorkingDirectory()
    {
        var options = BuildCodexAIAgentOptions(
            """
            {"threadOptions":{"model":"gpt-5-codex","skipGitRepoCheck":true}}
            """,
            "D:\\source\\workspace",
            threadId: null,
            resume: false
        );

        Assert.NotNull(options);
        Assert.Equal("D:\\source\\workspace", options.ThreadOptions.WorkingDirectory);
        Assert.Equal("gpt-5-codex", options.ThreadOptions.Model);
        Assert.True(options.ThreadOptions.SkipGitRepoCheck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildCodexAIAgentOptions_WhenProviderSessionProvided_UsesThreadId(bool resume)
    {
        var threadId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var options = BuildCodexAIAgentOptions("{}", workspace: null, threadId, resume);

        Assert.NotNull(options);
        Assert.Equal(threadId, options.ThreadId);
        Assert.Equal(resume, options.IsResume);
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenThreadStartedCallbackProvided_PreservesCallback()
    {
        ValueTask OnThreadStartedAsync(string threadId, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        Func<string, CancellationToken, ValueTask> callback = OnThreadStartedAsync;

        var options = BuildCodexAIAgentOptions(
            "{}",
            workspace: null,
            threadId: null,
            resume: false,
            environmentVariables: null,
            onThreadStartedAsync: callback
        );

        Assert.NotNull(options);
        Assert.Same(callback, options.OnThreadStartedAsync);
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenEnvironmentVariablesProvided_InheritsProcessEnvironment()
    {
        const string inheritedEnvName = "AGW_TEST_INHERITED_ENV";
        Environment.SetEnvironmentVariable(inheritedEnvName, "inherited");

        try
        {
            var options = BuildCodexAIAgentOptions(
                JsonUtil.Serialize(new CodexAIAgentOptions()),
                workspace: null,
                threadId: null,
                resume: false,
                new Dictionary<string, string> { ["AGW_TOKEN"] = "secret" }
            );

            Assert.NotNull(options);
            Assert.NotNull(options.CodexOptions.Env);
            Assert.Equal("inherited", options.CodexOptions.Env[inheritedEnvName]);
            Assert.Equal("secret", options.CodexOptions.Env["AGW_TOKEN"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(inheritedEnvName, null);
        }
    }

    [Fact]
    public void ExecutionContextIdResolver_WhenContextMissing_GeneratesNormalizedContextId()
    {
        var generatedContextId = ContextIdUtil.ResolveContextId(null);
        var suppliedContextId = ContextIdUtil.ResolveContextId(" context-1 ");

        Assert.False(string.IsNullOrWhiteSpace(generatedContextId));
        Assert.Equal(Guid.Parse(generatedContextId).Normalize(), generatedContextId);
        Assert.Equal("context-1", suppliedContextId);
    }

    private static CodexAIAgentOptions? BuildCodexAIAgentOptions(
        string extra,
        string? workspace,
        Guid? threadId,
        bool resume,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        Func<string, CancellationToken, ValueTask>? onThreadStartedAsync = null
    )
    {
        var method = typeof(AgentRuntimeService).GetMethod(
            "BuildCodexAIAgentOptions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic
        );

        Assert.NotNull(method);
        return Assert.IsType<CodexAIAgentOptions>(
            method.Invoke(null, [extra, workspace, threadId, resume, environmentVariables, onThreadStartedAsync])
        );
    }
}
