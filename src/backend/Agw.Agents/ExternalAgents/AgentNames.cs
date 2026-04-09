using Agw.Shared.Utils;

using ClaudeCodeSdk.MAF;

namespace Agw.Agents.ExternalAgents;

public class AgentNames
{
    public const string ClaudeCode = "ClaudeCode";
    public const string Codex = "Codex";
    public const string GithubCopilot = "GithubCopilot";

    public static readonly Guid ClaudeCodeId = Guid.Parse("11111111-1111-1111-2222-000000000001");
    public static readonly Guid CodexId = Guid.Parse("11111111-1111-1111-2222-000000000002");
    public static readonly Guid GithubCopilotId = Guid.Parse("11111111-1111-1111-2222-000000000003");

    public static IReadOnlyList<Agent> ExternalAgentNames { get; } =
        [
            new Agent
            {
                Id = ClaudeCodeId,
                DisplayName = "Claude Code",
                Name = ClaudeCode,
                Description = "External agent for Claude Code integration with AI-powered coding assistance",
                Type = AgentType.External,
                Extra = JsonUtil.Serialize(new ClaudeCodeAIAgentOptions()),
            },
            new Agent
            {
                Id = CodexId,
                DisplayName = "OpenAI Codex",
                Name = Codex,
                Description = "External agent for OpenAI Codex integration",
                Type = AgentType.External,
            },
            //new Agent
            //{
            //    Id = GithubCopilotId,
            //    DisplayName = "Github Copilot",
            //    Name = GithubCopilot,
            //    Description = "External agent for Github Copilot integration",
            //    Type = AgentType.External,
            //}
        ];
}
