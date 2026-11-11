using Agw.Shared.Exceptions;
using ClaudeCodeSdk.MAF;

namespace Agw.Agents.Execution.Agents.Runtime;

internal static class BackgroundToolPolicySupport
{
    public static bool SupportsNoTools(EngineKind engineKind) => engineKind is EngineKind.Maf or EngineKind.ClaudeCode;

    public static ClaudeCodeAIAgentOptions ApplyClaudeCode(
        ClaudeCodeAIAgentOptions options,
        BackgroundToolPolicy toolPolicy
    ) =>
        toolPolicy switch
        {
            BackgroundToolPolicy.Default => options,
            BackgroundToolPolicy.None => options with
            {
                DisallowedTools = ["*"],
                McpServers = null!,
                McpServersPath = null,
            },
            _ => throw new AgwException(ErrorCodes.InvalidParam, "The background tool policy is invalid."),
        };
}
