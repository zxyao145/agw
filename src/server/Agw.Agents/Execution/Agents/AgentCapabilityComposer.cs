using System.Diagnostics;

using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;
using Agw.Domain.Services;
using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Mcp;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tools;
using Agw.Tools.Runtime;
using Agw.Tools.ToolBlocks;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

/// <summary>
/// Composes the runtime capabilities required by a System Agent.
/// 聚合 System Agent 运行时所需的各类 Capability。
/// </summary>
/// <remarks>
/// Resolves tools, connections, MCP tool servers, and Agent/Project Tool Blocks,
/// validates tool-name conflicts, and returns their tools, providers, evaluators,
/// approval rules, warnings, and owned resources as one <see cref="AgentCapabilityComposition"/>.
/// 负责解析 Tool、Connection、MCP Tool Server 以及 Agent/Project ToolBlock，
/// 校验工具名称冲突，并将工具、Provider、Evaluator、审批规则、警告和所拥有的资源
/// 汇总为一个 <see cref="AgentCapabilityComposition"/>。
/// The returned composition owns all materialized resources and must be disposed with the Agent.
/// 返回的组合对象拥有所有物化资源，必须随 Agent 一同释放。
/// </remarks>
public sealed class AgentCapabilityComposer
{
    private readonly AgentAppService _agentAppService;
    private readonly ToolRegistryService _toolRegistry;
    private readonly IConnectionCapabilityResolver _connectionCapabilityResolver;
    private readonly IMcpToolMaterializer _mcpToolMaterializer;
    private readonly ToolBlockRegistry _toolBlockRegistry;
    private readonly ILogger<AgentCapabilityComposer> _logger;
    private readonly IReadOnlyList<IAgentInstructionsSource> _instructionSources;

    public AgentCapabilityComposer(
        AgentAppService agentAppService,
        ToolRegistryService toolRegistry,
        IConnectionCapabilityResolver connectionCapabilityResolver,
        IMcpToolMaterializer mcpToolMaterializer,
        ToolBlockRegistry toolBlockRegistry,
        ILogger<AgentCapabilityComposer> logger,
        IEnumerable<IAgentInstructionsSource>? instructionSources = null)
    {
        _agentAppService = agentAppService;
        _toolRegistry = toolRegistry;
        _connectionCapabilityResolver = connectionCapabilityResolver;
        _mcpToolMaterializer = mcpToolMaterializer;
        _toolBlockRegistry = toolBlockRegistry;
        _logger = logger;
        _instructionSources = instructionSources?.ToArray() ?? [];
    }

    internal async Task<AgentCapabilityComposition> ComposeAsync(
        Agent agent,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken,
        bool supportsHostedWebSearch = false,
        string defaultMode = "plan",
        Func<IReadOnlyList<Guid>, CancellationToken, ValueTask<IReadOnlyList<Microsoft.Agents.AI.AIAgent>>>?
            backgroundAgentFactory = null,
        Guid conversationId = default)
    {
        var lease = new AgentResourceLease();
        var tools = new List<AITool>();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginSkills = new List<PluginSkillReference>();
        var warnings = new List<ConnectionCapabilityWarning>();
        var contextProviders = new List<Microsoft.Agents.AI.AIContextProvider>();
        var loopEvaluators = new List<Microsoft.Agents.AI.LoopEvaluator>();
        var autoApprovalRules =
            new List<Func<Microsoft.Agents.AI.ToolAutoApprovalRuleContext, ValueTask<bool>>>();
        var planModeAllowedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toolWarnings = new List<string>();
        var toolInvocationWarnings = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            if (agent.Type == AgentType.External)
            {
                return new AgentCapabilityComposition(
                    tools,
                    pluginSkills,
                    warnings,
                    contextProviders,
                    loopEvaluators,
                    autoApprovalRules,
                    planModeAllowedToolNames,
                    toolWarnings,
                    toolInvocationWarnings,
                    lease);
            }

            contextProviders.Add(new AgwWorkspaceProvider(
                agent,
                project,
                _instructionSources,
                _logger));

            var resolvedToolValues = ToolValueResolution.Resolve(agent.Tools, project.Tools);
            var toolBlockDefinitions = resolvedToolValues.ToolBlocks
                .Where(definition =>
                    backgroundAgentFactory != null ||
                    !string.Equals(
                        definition.GetDefinitionName(),
                        ToolBlockNames.BackgroundAgents,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var materializationContext = new ToolMaterializationContext
            {
                Agent = agent,
                Project = project,
                ConversationId = conversationId,
                Workspace = project.Workspace ?? string.Empty,
                DefaultMode = defaultMode,
                EnvironmentVariables = environmentVariables,
                BackgroundAgentFactory = backgroundAgentFactory,
                SupportsHostedWebSearch = supportsHostedWebSearch
            };

            var toolContribution = await _toolRegistry
                .MaterializeAsync(
                    resolvedToolValues.Tools,
                    materializationContext,
                    cancellationToken)
                .ConfigureAwait(false);
            AddContribution(
                toolContribution,
                "built-in",
                tools,
                registeredToolNames,
                contextProviders,
                loopEvaluators,
                autoApprovalRules,
                planModeAllowedToolNames,
                toolWarnings,
                toolInvocationWarnings,
                lease);

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

            var contribution = await _toolBlockRegistry.MaterializeAsync(
                    toolBlockDefinitions,
                    ToolBlockScope.Agent | ToolBlockScope.Project,
                    materializationContext,
                    cancellationToken)
                .ConfigureAwait(false);
            AddContribution(
                contribution,
                "tool-block",
                tools,
                registeredToolNames,
                contextProviders,
                loopEvaluators,
                autoApprovalRules,
                planModeAllowedToolNames,
                toolWarnings,
                toolInvocationWarnings,
                lease);
            foreach (var warning in contribution.Warnings)
            {
                _logger.LogWarning(
                    "Tool Block warning for agent {AgentId}: {Warning}",
                    agent.Id,
                    warning);
            }

            return new AgentCapabilityComposition(
                tools,
                pluginSkills,
                warnings,
                contextProviders,
                loopEvaluators,
                autoApprovalRules,
                planModeAllowedToolNames,
                toolWarnings,
                toolInvocationWarnings,
                lease);
        }
        catch
        {
            await DisposeWithoutThrowingAsync(lease).ConfigureAwait(false);
            throw;
        }
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

    private static void AddContribution(
        ToolContribution contribution,
        string source,
        ICollection<AITool> tools,
        ISet<string> registeredToolNames,
        ICollection<Microsoft.Agents.AI.AIContextProvider> contextProviders,
        ICollection<Microsoft.Agents.AI.LoopEvaluator> loopEvaluators,
        ICollection<Func<Microsoft.Agents.AI.ToolAutoApprovalRuleContext, ValueTask<bool>>>
            autoApprovalRules,
        ISet<string> planModeAllowedToolNames,
        ICollection<string> warnings,
        IDictionary<string, string> invocationWarnings,
        AgentResourceLease lease)
    {
        lease.Add(contribution);
        AddTools(tools, registeredToolNames, contribution.Tools, source);
        planModeAllowedToolNames.UnionWith(contribution.PlanModeAllowedToolNames);
        foreach (var provider in contribution.ContextProviders)
        {
            contextProviders.Add(provider);
        }

        foreach (var evaluator in contribution.LoopEvaluators)
        {
            loopEvaluators.Add(evaluator);
        }

        foreach (var rule in contribution.AutoApprovalRules)
        {
            autoApprovalRules.Add(rule);
        }

        foreach (var warning in contribution.Warnings)
        {
            warnings.Add(warning);
        }

        foreach (var warning in contribution.InvocationWarnings)
        {
            invocationWarnings[warning.Key] = warning.Value;
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
