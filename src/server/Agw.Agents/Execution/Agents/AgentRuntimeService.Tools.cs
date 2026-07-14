using Agw.Agents.Definitions.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService
{
    private async Task<IList<AITool>?> CreateAgentTools(
        Agent agent,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var mergedTools = new List<AITool>();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var appInstanceIds = agent.AgentAppRelations
            .Select(relation => relation.AppInstanceId)
            .Concat(project.ProjectAppRelations.Select(relation => relation.AppInstanceId));
        var toolNames = await _agentAppService.CollectNamedToolNamesAsync(
            [agent.Tools, project.Tools],
            appInstanceIds);
        if (toolNames.Length > 0)
        {
            var functions =
                _toolRegistry.CreateAIFunctions(toolNames, project.Id);
            if (functions.Count > 0)
            {
                AddUniqueTools(mergedTools, registeredToolNames, functions);
            }
        }

        var mcpToolServerIds = agent.AgentMcpToolServers
            .Select(relation => relation.McpToolServerId)
            .Concat(project.ProjectMcpToolServers.Select(relation => relation.McpToolServerId));
        var mcpTools = await ListToolsAsync(mcpToolServerIds, environmentVariables, cancellationToken)
            .ConfigureAwait(false);
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

    private async Task<IReadOnlyList<AITool>> ListToolsAsync(
        IEnumerable<Guid> mcpToolServerIds,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var servers = await _agentAppService.ListEnabledMcpToolServersAsync(mcpToolServerIds);
        var tools = new List<AITool>();
        foreach (var server in servers)
        {
            try
            {
                var serverTools = await _mcpToolLister(server, environmentVariables, cancellationToken)
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

    private static async Task<IReadOnlyList<AITool>> ListMcpToolsAsync(
        McpServer server,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        return await McpToolServerToolClient.ListToolsAsync(
                server,
                environmentVariables,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
