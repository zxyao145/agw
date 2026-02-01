using ClaudeCodeSdk.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSystem.ExternalAgents.Tests;

internal sealed class TestClaudeCodeAIAgent : ClaudeCodeAIAgent
{
    public TestClaudeCodeAIAgent()
        : base(new ClaudeCodeAIAgentOptions(), NullLogger<ClaudeCodeAIAgent>.Instance)
    {
    }
}

internal sealed class TestAgentThread : AgentThread
{
}
