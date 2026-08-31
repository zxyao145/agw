using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.ExternalAgents;

/// <summary>
/// <para>表示用于运行时分派的已知外部 Agent 分类；该值不会持久化到数据库。</para>
/// <para>Represents a known external Agent category used for runtime dispatch; this value is not persisted.</para>
/// </summary>
internal enum ExternalAgentKind
{
    /// <summary>
    /// <para>未识别、未支持或不是外部 Agent。</para>
    /// <para>The Agent is unknown, unsupported, or not an external Agent.</para>
    /// </summary>
    None,

    /// <summary>
    /// <para>Claude Code 外部 Agent。</para>
    /// <para>The Claude Code external Agent.</para>
    /// </summary>
    ClaudeCode,

    /// <summary>
    /// <para>OpenAI Codex 外部 Agent。</para>
    /// <para>The OpenAI Codex external Agent.</para>
    /// </summary>
    Codex,

    /// <summary>
    /// <para>Pi 外部 Agent。</para>
    /// <para>The Pi external Agent.</para>
    /// </summary>
    Pi,
}

/// <summary>
/// <para>根据现有 Agent 类型和规范名称解析运行时外部 Agent 分类。</para>
/// <para>Resolves the runtime external Agent category from the existing Agent type and canonical name.</para>
/// </summary>
internal static class ExternalAgentKindResolver
{
    /// <summary>
    /// <para>使用不区分大小写的规范名称匹配解析外部 Agent 分类。</para>
    /// <para>Resolves an external Agent category by matching canonical names without regard to case.</para>
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

        if (string.Equals(agent.Name, AgentNames.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalAgentKind.ClaudeCode;
        }

        if (string.Equals(agent.Name, AgentNames.Codex, StringComparison.OrdinalIgnoreCase))
        {
            return ExternalAgentKind.Codex;
        }

        return string.Equals(agent.Name, AgentNames.Pi, StringComparison.OrdinalIgnoreCase)
            ? ExternalAgentKind.Pi
            : ExternalAgentKind.None;
    }
}
