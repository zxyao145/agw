using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Counts schema-enabled agent turn executions. Labels never include schema text, prompts, or
/// response content.
/// </summary>
public static class AgentResponseSchemaTelemetry
{
    private static readonly Meter Meter = new("Agw.Agents.Execution");

    private static readonly Counter<long> Executions = Meter.CreateCounter<long>(
        "agw_agent_response_schema_execution_total"
    );

    private static readonly Counter<long> FailedExecutions = Meter.CreateCounter<long>(
        "agw_agent_response_schema_execution_failed"
    );

    public static void Record(string agentType, string externalAgentKind, string providerType, bool failed)
    {
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
