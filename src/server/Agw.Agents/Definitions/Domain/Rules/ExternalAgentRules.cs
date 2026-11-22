using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Definitions.Domain.Rules;

public static class ExternalAgentRules
{
    public static IReadOnlyList<EngineKind> SupportedKinds { get; } =
    [EngineKind.ClaudeCode, EngineKind.Codex, EngineKind.Pi, EngineKind.A2A];

    public static bool UsesA2AEndpoint(Agent agent) =>
        agent.Type == AgentType.External && UsesA2AEndpoint(agent.ExternalAgentKind);

    public static bool UsesA2AEndpoint(EngineKind? kind) => kind == EngineKind.A2A;
}
