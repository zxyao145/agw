using System.Diagnostics;

using Agw.Agents.Definitions.Agents;
using Agw.Domain.Services;
using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Mcp;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public sealed class AgentCapabilityComposer
{
    private readonly AgentAppService _agentAppService;
    private readonly ToolRegistryService _toolRegistry;
    private readonly IConnectionCapabilityResolver _connectionCapabilityResolver;
    private readonly IMcpToolMaterializer _mcpToolMaterializer;
    private readonly ILogger<AgentCapabilityComposer> _logger;

    public AgentCapabilityComposer(
        AgentAppService agentAppService,
        ToolRegistryService toolRegistry,
        IConnectionCapabilityResolver connectionCapabilityResolver,
        IMcpToolMaterializer mcpToolMaterializer,
        ILogger<AgentCapabilityComposer> logger)
    {
        _agentAppService = agentAppService;
        _toolRegistry = toolRegistry;
        _connectionCapabilityResolver = connectionCapabilityResolver;
        _mcpToolMaterializer = mcpToolMaterializer;
        _logger = logger;
    }

    internal async Task<AgentCapabilityComposition> ComposeAsync(
        Agent agent,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var lease = new AgentResourceLease();
        var tools = new List<AITool>();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginSkills = new List<PluginSkillReference>();
        var warnings = new List<ConnectionCapabilityWarning>();

        try
        {
            if (agent.Type == AgentType.External)
            {
                return new AgentCapabilityComposition(tools, pluginSkills, warnings, lease);
            }

            await AddStatelessToolsAsync(agent, project, tools, registeredToolNames)
                .ConfigureAwait(false);

            var connectionIds = agent.AgentConnectionRelations
                .Select(relation => relation.ConnectionId)
                .Concat(project.ProjectConnectionRelations.Select(relation => relation.ConnectionId))
                .Where(static id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            if (connectionIds.Length > 0)
            {
                var resolution = await _connectionCapabilityResolver
                    .ResolveAsync(project.Id, connectionIds, cancellationToken)
                    .ConfigureAwait(false);
                lease.Add(resolution);
                AddTools(tools, registeredToolNames, resolution.Tools, "connection");
                pluginSkills.AddRange(resolution.PluginSkills);
                warnings.AddRange(resolution.Warnings);
                foreach (var warning in resolution.Warnings)
                {
                    _logger.LogWarning(
                        "Connection capability warning {WarningCode} for connection {ConnectionId}",
                        warning.Code,
                        warning.ConnectionId);
                    Activity.Current?.AddEvent(new ActivityEvent(
                        "agw.integration.warning",
                        tags: new ActivityTagsCollection
                        {
                            { "agw.integration.warning.code", warning.Code },
                            { "agw.integration.connection.id", warning.ConnectionId.ToString() },
                        }));
                }
            }

            await AddIndependentMcpToolsAsync(
                    agent,
                    project,
                    environmentVariables,
                    tools,
                    registeredToolNames,
                    lease,
                    cancellationToken)
                .ConfigureAwait(false);

            return new AgentCapabilityComposition(tools, pluginSkills, warnings, lease);
        }
        catch
        {
            await DisposeWithoutThrowingAsync(lease).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AddStatelessToolsAsync(
        Agent agent,
        Project project,
        ICollection<AITool> tools,
        ISet<string> registeredToolNames)
    {
        var toolNames = await _agentAppService
            .CollectNamedToolNamesAsync([agent.Tools, project.Tools])
            .ConfigureAwait(false);
        if (toolNames.Length == 0)
        {
            return;
        }

        AddTools(
            tools,
            registeredToolNames,
            _toolRegistry.CreateAIFunctions(toolNames, project.Id),
            "built-in");
    }

    private async Task AddIndependentMcpToolsAsync(
        Agent agent,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        ICollection<AITool> tools,
        ISet<string> registeredToolNames,
        AgentResourceLease lease,
        CancellationToken cancellationToken)
    {
        var serverIds = agent.AgentMcpToolServers
            .Select(relation => relation.McpToolServerId)
            .Concat(project.ProjectMcpToolServers.Select(relation => relation.McpToolServerId));
        var servers = await _agentAppService.ListEnabledMcpToolServersAsync(serverIds).ConfigureAwait(false);
        foreach (var server in servers)
        {
            try
            {
                var descriptor = CreateDescriptor(server);
                var materialized = await _mcpToolMaterializer
                    .MaterializeAsync(
                        descriptor,
                        new McpRuntimeOverrides(environmentVariables),
                        cancellationToken)
                    .ConfigureAwait(false);
                lease.Add(materialized);
                AddTools(tools, registeredToolNames, materialized.Tools, $"mcp:{server.Id}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AgwException exception) when (
                exception.Code == ErrorCodes.IntegrationToolNameInvalid.Code ||
                exception.Code == ErrorCodes.IntegrationToolNameConflict.Code)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to materialize independent MCP server {ServerId}",
                    server.Id);
            }
        }
    }

    private static McpEndpointDescriptor CreateDescriptor(McpServer server)
    {
        return server.TransportType.ToLowerInvariant() switch
        {
            "stdio" => new McpStdioEndpointDescriptor(
                server.Name,
                server.Command,
                server.Arguments,
                server.WorkingDirectory,
                server.EnvironmentVariables),
            "http" => new McpHttpEndpointDescriptor(
                server.Name,
                CreateEndpoint(server.Url),
                server.Headers),
            "sse" => new McpSseEndpointDescriptor(
                server.Name,
                CreateEndpoint(server.Url),
                server.Headers),
            _ => throw new AgwException(ErrorCodes.UnsupportedTransportType),
        };
    }

    private static Uri? CreateEndpoint(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ? endpoint : null;
    }

    private static void AddTools(
        ICollection<AITool> destination,
        ISet<string> registeredToolNames,
        IEnumerable<AITool> tools,
        string source)
    {
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new AgwException(
                    ErrorCodes.IntegrationToolNameInvalid,
                    $"Capability source '{source}' produced a tool without a name.");
            }

            if (!registeredToolNames.Add(tool.Name))
            {
                throw new AgwException(
                    ErrorCodes.IntegrationToolNameConflict,
                    $"Tool name '{tool.Name}' conflicts with another agent capability source.");
            }

            destination.Add(tool);
        }
    }

    private static async ValueTask DisposeWithoutThrowingAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
