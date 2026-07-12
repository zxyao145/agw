using Agw.Agents.Definitions.Agents;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    private async Task<IList<AITool>?> CreateAgentTools(
        Agent agent,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var mergedTools = new List<AITool>();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var toolNames = await _agentAppService.CollectNamedToolNamesAsync(agent.Id, agent.Tools);
        if (toolNames.Length > 0)
        {
            var functions =
                _toolRegistry.CreateAIFunctions(toolNames, projectId);
            if (functions.Count > 0)
            {
                AddUniqueTools(mergedTools, registeredToolNames, functions);
            }
        }

        var mcpTools = await ListToolsByAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        if (mcpTools.Count > 0)
        {
            AddUniqueTools(mergedTools, registeredToolNames, mcpTools);
        }

        return mergedTools.Count > 0 ? mergedTools : null;
    }

    private static void AddUniqueTools(
        ICollection<AITool> destination,
        ISet<string> registeredToolNames,
        IEnumerable<AITool> tools)
    {
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || !registeredToolNames.Add(tool.Name))
            {
                continue;
            }

            destination.Add(tool);
        }
    }

    private async Task<IReadOnlyList<McpClientTool>> ListToolsByAgentAsync(
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var servers = await _agentAppService.ListEnabledMcpToolServersByAgentAsync(agentId);
        var tools = new List<McpClientTool>();
        foreach (var server in servers)
        {
            try
            {
                var serverTools = await McpToolServerToolClient.ListToolsAsync(server, cancellationToken)
                    .ConfigureAwait(false);
                if (serverTools.Count > 0)
                {
                    tools.AddRange(serverTools);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list MCP tools from server {ServerId}", server.Id);
            }
        }

        return tools;
    }
}
