using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.ExternalAgents;

/// <summary>
/// <para>根据 Agent 类型和持久化分类解析运行时外部 Agent 分类。</para>
/// <para>Resolves the runtime external Agent category from the Agent type and persisted kind.</para>
/// </summary>
internal static class ExternalAgentKindResolver
{
    /// <summary>
    /// <para>非外部 Agent 和不支持的分类返回 <see cref="ExternalAgentKind.None"/>。</para>
    /// <para>Returns <see cref="ExternalAgentKind.None"/> for non-external Agents and unsupported kinds.</para>
    /// </summary>
    /// <param name="agent">
    /// <para>待分类的 Agent。</para>
    /// <para>The Agent to classify.</para>
    /// </param>
    /// <returns>
    /// <para>已知分类；非外部或未知 Agent 返回 <see cref="ExternalAgentKind.None"/>。</para>
    /// <para>The known category, or <see cref="ExternalAgentKind.None"/> for non-external or unknown Agents.</para>
    /// </returns>
    public static ExternalAgentKind Resolve(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (agent.Type != AgentType.External)
        {
            return ExternalAgentKind.None;
        }

        return agent.ExternalAgentKind switch
        {
            ExternalAgentKind.ClaudeCode => ExternalAgentKind.ClaudeCode,
            ExternalAgentKind.Codex => ExternalAgentKind.Codex,
            ExternalAgentKind.Pi => ExternalAgentKind.Pi,
            _ => ExternalAgentKind.None,
        };
    }
}
