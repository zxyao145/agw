using Agw.Agents.Application.AgentRun;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

using OpenAI.CodexSdk;
using OpenAI.CodexSdk.MAF;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceCompositionTests
{
    [Fact]
    public void MergeExtraSettings_WhenMultipleSourcesProvided_MergesInExpectedOverrideOrder()
    {
        var merged = AgentRuntimeServiceUtil.MergeExtraSettings(
            """
            {"temperature":0.1,"source":"agent","agentOnly":true}
            """,
            """
            {"temperature":0.3,"source":"project","projectOnly":true}
            """,
            """
            {"temperature":0.7,"source":"request","requestOnly":true}
            """);

        Assert.NotNull(merged);
        Assert.Contains("\"temperature\":0.7", merged, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"request\"", merged, StringComparison.Ordinal);
        Assert.Contains("\"agentOnly\":true", merged, StringComparison.Ordinal);
        Assert.Contains("\"projectOnly\":true", merged, StringComparison.Ordinal);
        Assert.Contains("\"requestOnly\":true", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeExtraSettings_WhenAllInputsInvalid_ReturnsNull()
    {
        var merged = AgentRuntimeServiceUtil.MergeExtraSettings(
            "not-json",
            "[]",
            "   ");

        Assert.Null(merged);
    }

    [Fact]
    public void BuildInstructions_WhenPromptAndWorkspaceProvided_AppendsWorkspaceInstruction()
    {
        var instructions = AgentRuntimeServiceUtil.BuildInstructions("System prompt", "/tmp/workspace");

        Assert.Contains("System prompt", instructions, StringComparison.Ordinal);
        Assert.Contains("default workspace or working directory", instructions, StringComparison.Ordinal);
        Assert.Contains("/tmp/workspace", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstructions_WhenPromptMissing_UsesDefaultPrompt()
    {
        var instructions = AgentRuntimeServiceUtil.BuildInstructions("   ", null);

        Assert.Equal("You are an AI agent.", instructions);
    }

    [Fact]
    public void ExternalAgentNames_Codex_HasDefaultCodexOptions()
    {
        var codexAgent = Assert.Single(
            AgentNames.ExternalAgentNames,
            agent => agent.Name == AgentNames.Codex);

        Assert.False(string.IsNullOrWhiteSpace(codexAgent.Extra));
        var options = JsonUtil.Deserialize<CodexAIAgentOptions>(codexAgent.Extra!);

        Assert.NotNull(options);
        Assert.NotNull(options.CodexOptions);
        Assert.NotNull(options.ThreadOptions);
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenWorkspaceProvided_SetsThreadWorkingDirectory()
    {
        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            """
            {"threadOptions":{"model":"gpt-5-codex","skipGitRepoCheck":true}}
            """,
            "D:\\source\\workspace",
            threadId: null,
            resume: false);

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

        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            "{}",
            workspace: null,
            threadId,
            resume);

        Assert.NotNull(options);
        Assert.Equal(threadId, options.ThreadId);
        Assert.Equal(resume, options.IsResume);
    }

    [Fact]
    public void BuildCodexAIAgentOptions_WhenThreadStartedCallbackProvided_PreservesCallback()
    {
        ValueTask OnThreadStartedAsync(string threadId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        Func<string, CancellationToken, ValueTask> callback = OnThreadStartedAsync;

        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            "{}",
            workspace: null,
            threadId: null,
            resume: false,
            environmentVariables: null,
            onThreadStartedAsync: callback);

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
            var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
                JsonUtil.Serialize(new CodexAIAgentOptions()),
                workspace: null,
                threadId: null,
                resume: false,
                new Dictionary<string, string>
                {
                    ["AGW_TOKEN"] = "secret"
                });

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
    public async Task CreateCodexAgent_WhenResumeRequested_CreatesSessionForTaskThread()
    {
        var service = new AgentRuntimeService(
            agentAppService: null!,
            projectAppService: null!,
            toolRegistry: null!,
            cache: null!,
            chatHistoryProvider: null!,
            providerSessionState: null!,
            projectTaskSessionBindingService: null!,
            webHostEnvironment: null!,
            fileSystemResolver: null!,
            logger: NullLogger<AgentRuntimeService>.Instance);
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateCodexAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [
                typeof(string),
                typeof(string),
                typeof(Guid?),
                typeof(bool),
                typeof(IReadOnlyDictionary<string, string>),
                typeof(Func<string, CancellationToken, ValueTask>)
            ]);
        var providerThreadId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var extra = JsonUtil.Serialize(new CodexAIAgentOptions());

        Assert.NotNull(method);
        var agent = Assert.IsAssignableFrom<AIAgent>(
            method.Invoke(service, [extra, null, providerThreadId, true, null, null]));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var codexSession = Assert.IsType<CodexAgentSession>(session);

        Assert.Equal(providerThreadId.Normalize(), codexSession.ThreadId);
    }

    [Fact]
    public void CreateCodexAgent_WhenThreadStartedCallbackProvided_PassesCallbackToOptions()
    {
        var service = CreateRuntimeServiceForReflection();
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateCodexAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [
                typeof(string),
                typeof(string),
                typeof(Guid?),
                typeof(bool),
                typeof(IReadOnlyDictionary<string, string>),
                typeof(Func<string, CancellationToken, ValueTask>)
            ]);
        var extra = JsonUtil.Serialize(new CodexAIAgentOptions());
        ValueTask OnThreadStartedAsync(string threadId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        Func<string, CancellationToken, ValueTask> callback = OnThreadStartedAsync;

        Assert.NotNull(method);
        var agent = Assert.IsAssignableFrom<AIAgent>(
            method.Invoke(service, [extra, null, null, false, null, callback]));
        var options = GetPrivateField<CodexAIAgentOptions>(agent, "_options");

        Assert.Same(callback, options.OnThreadStartedAsync);
    }

    [Fact]
    public void CreateClaudeCodeAgent_WhenEnvironmentVariablesProvided_PassesEnvironmentVariablesToOptions()
    {
        var service = CreateRuntimeServiceForReflection();
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateClaudeCodeAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [
                typeof(string),
                typeof(string),
                typeof(Guid?),
                typeof(bool),
                typeof(IReadOnlyDictionary<string, string>)
            ]);
        var extra = JsonUtil.Serialize(new ClaudeCodeAIAgentOptions());
        var environmentVariables = new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "secret"
        };

        Assert.NotNull(method);
        var agent = Assert.IsAssignableFrom<AIAgent>(
            method.Invoke(service, [extra, null, null, false, environmentVariables]));
        var options = GetPrivateField<ClaudeCodeAIAgentOptions>(agent, "_options");

        Assert.NotNull(options.EnvironmentVariables);
        Assert.Equal("secret", options.EnvironmentVariables["AGW_TOKEN"]);
    }

    [Fact]
    public void CreateCodexAgent_WhenEnvironmentVariablesProvided_PassesEnvironmentVariablesToOptions()
    {
        var service = CreateRuntimeServiceForReflection();
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateCodexAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            [
                typeof(string),
                typeof(string),
                typeof(Guid?),
                typeof(bool),
                typeof(IReadOnlyDictionary<string, string>),
                typeof(Func<string, CancellationToken, ValueTask>)
            ]);
        var extra = JsonUtil.Serialize(new CodexAIAgentOptions
        {
            CodexOptions = new CodexOptions
            {
                Env = new Dictionary<string, string>
                {
                    ["EXISTING"] = "kept",
                    ["AGW_TOKEN"] = "old"
                }
            }
        });
        var environmentVariables = new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "secret"
        };

        Assert.NotNull(method);
        var agent = Assert.IsAssignableFrom<AIAgent>(
            method.Invoke(service, [extra, null, null, false, environmentVariables, null]));
        var options = GetPrivateField<CodexAIAgentOptions>(agent, "_options");

        Assert.NotNull(options.CodexOptions.Env);
        Assert.Equal("kept", options.CodexOptions.Env["EXISTING"]);
        Assert.Equal("secret", options.CodexOptions.Env["AGW_TOKEN"]);
    }

    private static AgentRuntimeService CreateRuntimeServiceForReflection()
    {
        return new AgentRuntimeService(
            agentAppService: null!,
            projectAppService: null!,
            toolRegistry: null!,
            cache: null!,
            chatHistoryProvider: null!,
            providerSessionState: null!,
            projectTaskSessionBindingService: null!,
            webHostEnvironment: null!,
            fileSystemResolver: null!,
            logger: NullLogger<AgentRuntimeService>.Instance);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field.GetValue(instance));
    }
}
