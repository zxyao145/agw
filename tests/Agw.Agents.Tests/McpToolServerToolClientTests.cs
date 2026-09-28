using Agw.Agents.Definitions.Agents;
using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Tests;

public class McpToolServerToolClientTests
{
    [Theory]
    [InlineData("~/MCP Tools/server", "~/.codex")]
    [InlineData("~\\MCP Tools/server", "~\\.codex")]
    [InlineData("~/MCP Tools/server", "~")]
    public void CreateStdioTransportOptions_HomePaths_ExpandsCommandAndWorkingDirectory(
        string command,
        string workingDirectory
    )
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var server = new McpServer
        {
            Name = "stdio",
            Command = command,
            WorkingDirectory = workingDirectory,
            Arguments = ["mcp", "~/argument", "literal~value"],
        };

        var options = McpToolServerToolClient.CreateStdioTransportOptions(server);

        Assert.Equal(Path.Combine(userHome, "MCP Tools/server"), options.Command);
        Assert.Equal(workingDirectory == "~" ? userHome : Path.Combine(userHome, ".codex"), options.WorkingDirectory);
        Assert.Equal(server.Arguments, options.Arguments);
        Assert.Equal(command, server.Command);
        Assert.Equal(workingDirectory, server.WorkingDirectory);
    }

    [Theory]
    [InlineData("npx", null)]
    [InlineData("./server~name", "./workspace")]
    public void CreateStdioTransportOptions_OrdinaryPaths_PreservesCommandAndWorkingDirectory(
        string command,
        string? workingDirectory
    )
    {
        var server = new McpServer
        {
            Name = "stdio",
            Command = command,
            WorkingDirectory = workingDirectory,
        };

        var options = McpToolServerToolClient.CreateStdioTransportOptions(server);

        Assert.Equal(command, options.Command);
        Assert.Equal(workingDirectory, options.WorkingDirectory);
    }

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
