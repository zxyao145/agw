using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agw.Agents.Execution.Agents.Middleware.Telemetry;

/// <summary>
/// <para>发布结构化输出执行总数和失败数，使用固定维度描述执行来源。</para>
/// <para>Publishes total and failed structured-output execution counts with fixed attribution dimensions.</para>
/// </summary>
/// <remarks>
/// <para>指标标签仅包含 Agent 类型、外部引擎类型、Provider 类型和结果，不包含 Schema、提示词或响应正文。</para>
/// <para>Metric labels contain only agent type, external engine kind, provider type, and outcome, never schemas, prompts, or response bodies.</para>
/// </remarks>
public static class AgentResponseSchemaTelemetry
{
    private static readonly Meter Meter = new("Agw.Agents.Execution");

    private static readonly Counter<long> Executions = Meter.CreateCounter<long>(
        "agw_agent_response_schema_execution_total"
    );

    private static readonly Counter<long> FailedExecutions = Meter.CreateCounter<long>(
        "agw_agent_response_schema_execution_failed"
    );

    /// <summary>
    /// <para>递增结构化输出执行计数，并仅在失败时递增失败计数。</para>
    /// <para>Increments structured-output execution counts and increments failure counts only for failed executions.</para>
    /// </summary>
    /// <param name="agentType">
    /// <para>用于指标归属的 Agent 类型标签。</para>
    /// <para>Agent-type label used for metric attribution.</para>
    /// </param>
    /// <param name="externalAgentKind">
    /// <para>外部引擎类型标签；非外部 Agent 使用约定占位值。</para>
    /// <para>External-engine label, using the established sentinel for non-external agents.</para>
    /// </param>
    /// <param name="providerType">
    /// <para>模型 Provider 类型标签。</para>
    /// <para>Model-provider type label.</para>
    /// </param>
    /// <param name="failed">
    /// <para>本次执行是否满足结构化输出失败判定。</para>
    /// <para>Whether this execution meets the structured-output failure criteria.</para>
    /// </param>
    public static void Record(string agentType, string externalAgentKind, string providerType, bool failed)
    {
        // 标签只使用稳定分类信息，避免把 Schema 或消息正文引入指标维度。
        // Use stable classification labels only, keeping schemas and message bodies out of metric dimensions.
        var tags = new TagList
        {
            { "agent_type", agentType },
            { "external_agent_kind", externalAgentKind },
            { "provider_type", providerType },
            { "result", failed ? "failed" : "success" },
        };
        Executions.Add(1, tags);
        if (failed)
        {
            FailedExecutions.Add(1, tags);
        }
    }
}
