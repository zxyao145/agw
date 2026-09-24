using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Agents.ExternalAgents;

/// <summary>
/// 解析执行 Agent 定义的 Engine 种类。
/// Resolves the Engine kind that executes an Agent definition.
/// </summary>
internal static class EngineKinds
{
    public static EngineKind Resolve(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return Resolve(agent.Type, agent.ExternalAgentKind);
    }

    /// <summary>
    /// System Agent 使用 MAF；外部 Agent 使用持久化的种类，不支持的种类直接报错。
    /// System Agents use MAF; external Agents use the persisted kind and unsupported kinds fail.
    /// </summary>
    public static EngineKind Resolve(AgentType type, EngineKind persistedKind) =>
        type == AgentType.System ? EngineKind.Maf
        : persistedKind is EngineKind.ClaudeCode or EngineKind.Codex or EngineKind.Pi ? persistedKind
        : throw new AgwException(
            ErrorCodes.UnsupportedAgentType,
            $"External Agent kind '{persistedKind}' is not supported."
        );
}
