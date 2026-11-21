using Agw.Agents.Execution.Agents.Runtime;
using ClaudeCodeSdk.MAF;

namespace Agw.Agents.Tests;

public sealed class BackgroundToolPolicySupportTests
{
    [Theory]
    [InlineData(EngineKind.Maf, true)]
    [InlineData(EngineKind.ClaudeCode, true)]
    [InlineData(EngineKind.Codex, false)]
    [InlineData(EngineKind.Pi, false)]
    public void SupportsNoTools_ReturnsEngineCapability(EngineKind engineKind, bool expected)
    {
        Assert.Equal(expected, BackgroundToolPolicySupport.SupportsNoTools(engineKind));
    }

    [Fact]
    public void ApplyClaudeCode_None_RemovesToolAndMcpConfiguration()
    {
        var result = BackgroundToolPolicySupport.ApplyClaudeCode(
            new ClaudeCodeAIAgentOptions(),
            BackgroundToolPolicy.None
        );

        Assert.Equal(["*"], result.DisallowedTools);
        Assert.Null(result.McpServers);
        Assert.Null(result.McpServersPath);
    }
}
