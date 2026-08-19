using Agw.Agents.Definitions.Agents;

namespace Agw.Agents.Tests;

public class McpToolServerToolClientTests
{
    [Fact]
    public void MergeEnvironmentVariables_EffectiveAgentValuesOverrideServerValues()
    {
        var serverVariables = new Dictionary<string, string> { ["SHARED"] = "server", ["SERVER_ONLY"] = "server" };
        var effectiveAgentVariables = new Dictionary<string, string>
        {
            ["SHARED"] = "session",
            ["AGENT_ONLY"] = "agent",
        };

        var result = McpToolServerToolClient.MergeEnvironmentVariables(serverVariables, effectiveAgentVariables);

        Assert.NotNull(result);
        Assert.Equal("session", result["SHARED"]);
        Assert.Equal("server", result["SERVER_ONLY"]);
        Assert.Equal("agent", result["AGENT_ONLY"]);
    }

    [Fact]
    public void MergeEnvironmentVariables_WithNoValues_ReturnsNull()
    {
        var result = McpToolServerToolClient.MergeEnvironmentVariables(new Dictionary<string, string>(), null);

        Assert.Null(result);
    }
}
