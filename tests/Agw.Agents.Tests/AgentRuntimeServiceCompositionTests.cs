using Agw.Appliaction.Services.Agents;

namespace Agw.Agents.Tests;

public class AgentRuntimeServiceCompositionTests
{
    [Fact]
    public void MergeExtraSettings_WhenMultipleSourcesProvided_MergesInExpectedOverrideOrder()
    {
        var merged = AgentRuntimeConfigurationMerger.MergeExtraSettings(
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
        var merged = AgentRuntimeConfigurationMerger.MergeExtraSettings(
            "not-json",
            "[]",
            "   ");

        Assert.Null(merged);
    }

    [Fact]
    public void BuildInstructions_WhenPromptAndWorkspaceProvided_AppendsWorkspaceInstruction()
    {
        var instructions = AgentRuntimeInstructions.BuildInstructions("System prompt", "/tmp/workspace");

        Assert.Contains("System prompt", instructions, StringComparison.Ordinal);
        Assert.Contains("default workspace or working directory", instructions, StringComparison.Ordinal);
        Assert.Contains("/tmp/workspace", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstructions_WhenPromptMissing_UsesDefaultPrompt()
    {
        var instructions = AgentRuntimeInstructions.BuildInstructions("   ", null);

        Assert.Equal("You are an AI agent.", instructions);
    }
}
