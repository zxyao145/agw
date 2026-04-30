using Agw.Agents.Application.AgentRun;
using Agw.Agents.ExternalAgents;
using Agw.Shared.Extensions;
using Agw.Shared.Utils;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

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
            taskId: null,
            resume: false);

        Assert.NotNull(options);
        Assert.Equal("D:\\source\\workspace", options.ThreadOptions.WorkingDirectory);
        Assert.Equal("gpt-5-codex", options.ThreadOptions.Model);
        Assert.True(options.ThreadOptions.SkipGitRepoCheck);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildCodexAIAgentOptions_WhenTaskProvided_UsesTaskIdAsThreadId(bool resume)
    {
        var taskId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var options = AgentRuntimeServiceUtil.BuildCodexAIAgentOptions(
            "{}",
            workspace: null,
            taskId,
            resume);

        Assert.NotNull(options);
        Assert.Equal(taskId, options.ThreadId);
        Assert.Equal(resume, options.IsResume);
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
            webHostEnvironment: null!,
            logger: NullLogger<AgentRuntimeService>.Instance);
        var method = typeof(AgentRuntimeService).GetMethod(
            "CreateCodexAgent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var taskId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var extra = JsonUtil.Serialize(new CodexAIAgentOptions());

        Assert.NotNull(method);
        var agent = Assert.IsAssignableFrom<AIAgent>(method.Invoke(service, [extra, null, taskId, true]));
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var codexSession = Assert.IsType<CodexAgentSession>(session);

        Assert.Equal(taskId.Normalize(), codexSession.ThreadId);
    }
}
