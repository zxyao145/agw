using System.Diagnostics;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Context.Workspace;
using Agw.Extensions.Contracts;
using Agw.Integrations.Contracts.Capabilities;
using Agw.Integrations.Mcp;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Shared.Utils;
using Agw.Tools;
using Agw.Tools.Abstractions.ToolBlocks;
using Agw.Tools.HumanInteraction;
using Agw.Tools.Runtime;
using Agw.Tools.ToolBlocks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Composition;

/// <summary>
/// Composes the runtime capabilities required by an Agent.
/// 聚合 Agent 运行时所需的各类 Capability。
/// </summary>
/// <remarks>
/// Resolves tools, connections, MCP tool servers, and Agent/Project Tool Blocks,
/// validates tool-name conflicts, and returns their tools, providers, evaluators,
/// approval rules, warnings, and owned resources as one <see cref="AgentCapabilityComposition"/>.
/// External Agents materialize only the User Memory context provider and ignore all other capability sources.
/// 负责解析 Tool、Connection、MCP Tool Server 以及 Agent/Project ToolBlock，
/// 校验工具名称冲突，并将工具、Provider、Evaluator、审批规则、警告和所拥有的资源
/// 汇总为一个 <see cref="AgentCapabilityComposition"/>。
/// External Agent 仅物化 User Memory 上下文 Provider，并忽略其他 Capability 来源。
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
    private readonly IExtensionCapabilityCatalog _extensionCapabilityCatalog;

    public AgentCapabilityComposer(
        AgentAppService agentAppService,
        ToolRegistryService toolRegistry,
        IConnectionCapabilityResolver connectionCapabilityResolver,
        IMcpToolMaterializer mcpToolMaterializer,
        ToolBlockRegistry toolBlockRegistry,
        ILogger<AgentCapabilityComposer> logger,
        IExtensionCapabilityCatalog extensionCapabilityCatalog,
        IEnumerable<IAgentInstructionsSource>? instructionSources = null
    )
    {
        _agentAppService = agentAppService;
        _toolRegistry = toolRegistry;
        _connectionCapabilityResolver = connectionCapabilityResolver;
        _mcpToolMaterializer = mcpToolMaterializer;
        _toolBlockRegistry = toolBlockRegistry;
        _logger = logger;
        _instructionSources = instructionSources?.ToArray() ?? [];
        _extensionCapabilityCatalog = extensionCapabilityCatalog;
    }

    internal async Task<AgentCapabilityComposition> ComposeAsync(
        Agent agent,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken,
        bool supportsHostedWebSearch = false,
        string defaultMode = "execute",
        Func<
            IReadOnlyList<Guid>,
            CancellationToken,
            ValueTask<IReadOnlyList<Microsoft.Agents.AI.AIAgent>>
        >? backgroundAgentFactory = null,
        Guid conversationId = default,
        bool deferHumanInteractions = false,
        IReadOnlyList<ExtensionExecutionPin>? extensionPins = null,
        ExtensionCapabilitySnapshot? acceptedExtensionCapabilities = null,
        IReadOnlyList<Guid>? extensionIds = null,
        BackgroundToolPolicy toolPolicy = BackgroundToolPolicy.Default
    )
    {
        var lease = new AgentResourceLease();
        var combined = new ToolContribution();
        var registeredToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginSkills = new List<PluginSkillReference>();
        var warnings = new List<ConnectionCapabilityWarning>();
        AgentCapabilityComposition BuildComposition(IReadOnlyList<AgentSkill>? extensionSkills = null) =>
            new(combined, pluginSkills, warnings, lease, extensionSkills);

        try
        {
            var resolvedToolValues =
                agent.Type == AgentType.External
                    ? ToolValueResolution.Resolve(
                        SelectUserMemoryValues(agent.Tools),
                        SelectUserMemoryValues(project.Tools)
                    )
                    : ToolValueResolution.Resolve(agent.Tools, project.Tools);
            var extensionToolDefinitions = resolvedToolValues.Tools.OfType<ExtensionToolDefinition>().ToArray();
            var executableToolDefinitions = resolvedToolValues
                .Tools.Where(static definition => definition is not ExtensionToolDefinition)
                .OrderBy(static definition =>
                    GetDefinitionOrder(definition.GetDefinitionName(), ToolDefinitionNames.All)
                )
                .ToArray();
            var materializationContext = new ToolMaterializationContext
            {
                Agent = agent,
                Project = project,
                ConversationId = conversationId,
                Workspace = project.Workspace ?? string.Empty,
                WorkspaceSnapshot =
                    ExecutionContextSlot.GetWorkspaceSnapshot(project.Id)
                    ?? ProjectWorkspacePaths.CreateSnapshot(
                        project.Id,
                        project.Workspace,
                        (project.AdditionalDirectories ?? []).Select(directory => new ProjectWorkspaceDirectory(
                            directory.Id,
                            directory.Path
                        ))
                    ),
                DefaultMode = defaultMode,
                EnvironmentVariables = environmentVariables,
                BackgroundAgentFactory = backgroundAgentFactory,
                SupportsHostedWebSearch = supportsHostedWebSearch,
            };

            if (agent.Type == AgentType.External)
            {
                if (toolPolicy == BackgroundToolPolicy.None)
                {
                    return BuildComposition();
                }

                var userMemoryDefinitions = resolvedToolValues
                    .ToolBlocks.Where(definition =>
                        string.Equals(
                            definition.GetDefinitionName(),
                            ToolBlockNames.UserMemory,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToArray();
                if (userMemoryDefinitions.Length > 0)
                {
                    var userMemoryContribution = await _toolBlockRegistry
                        .MaterializeAsync(
                            userMemoryDefinitions,
                            ToolBlockScope.Agent | ToolBlockScope.Project,
                            materializationContext,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    lease.Add(userMemoryContribution);
                    combined.ContextProviders.AddRange(userMemoryContribution.ContextProviders);
                }

                return BuildComposition();
            }

            var workspaceProvider = new AgwWorkspaceProvider(agent, project, _instructionSources, _logger);

            if (toolPolicy == BackgroundToolPolicy.None)
            {
                combined.ContextProviders.Add(workspaceProvider);
                return BuildComposition();
            }

            var toolBlockDefinitions = resolvedToolValues
                .ToolBlocks.Where(definition =>
                    backgroundAgentFactory != null
                    || !string.Equals(
                        definition.GetDefinitionName(),
                        ToolBlockNames.BackgroundAgents,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .OrderBy(static definition =>
                    GetDefinitionOrder(definition.GetDefinitionName(), ToolBlockDefinitionNames.All)
                )
                .ToArray();

            var toolContribution = await _toolRegistry
                .MaterializeAsync(executableToolDefinitions, materializationContext, cancellationToken)
                .ConfigureAwait(false);
            AddContribution(combined, toolContribution, registeredToolNames, "built-in", lease);

            var connectionIds = agent
                .AgentConnectionRelations.Select(relation => relation.ConnectionId)
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
                AddTools(combined.Tools, registeredToolNames, resolution.Tools, "connection");
                pluginSkills.AddRange(resolution.PluginSkills);
                warnings.AddRange(resolution.Warnings);
                foreach (var warning in resolution.Warnings)
                {
                    _logger.LogWarning(
                        "Connection capability warning {WarningCode} for connection {ConnectionId}",
                        warning.Code,
                        warning.ConnectionId
                    );
                    Activity.Current?.AddEvent(
                        new ActivityEvent(
                            "agw.integration.warning",
                            tags: new ActivityTagsCollection
                            {
                                { "agw.integration.warning.code", warning.Code },
                                { "agw.integration.connection.id", warning.ConnectionId.ToString() },
                            }
                        )
                    );
                }
            }

            await AddIndependentMcpToolsAsync(
                    agent,
                    project,
                    environmentVariables,
                    combined.Tools,
                    registeredToolNames,
                    lease,
                    cancellationToken
                )
                .ConfigureAwait(false);

            var contribution = await _toolBlockRegistry
                .MaterializeAsync(
                    toolBlockDefinitions,
                    ToolBlockScope.Agent | ToolBlockScope.Project,
                    materializationContext,
                    cancellationToken
                )
                .ConfigureAwait(false);
            AddContribution(combined, contribution, registeredToolNames, "tool-block", lease);
            foreach (var warning in contribution.Warnings)
            {
                _logger.LogWarning("Tool Block warning for agent {AgentId}: {Warning}", agent.Id, warning);
            }

            IReadOnlyList<Guid> skillExtensionIds =
                acceptedExtensionCapabilities == null && extensionPins == null && extensionIds == null
                    ? await _extensionCapabilityCatalog
                        .ListEnabledSkillExtensionIdsAsync(cancellationToken)
                        .ConfigureAwait(false)
                    : [];
            var extensionSelection = new ExtensionCapabilitySelection(
                extensionToolDefinitions
                    .Select(definition => new ExtensionToolSelection(definition.ExtensionId, definition.ToolId))
                    .Distinct()
                    .ToArray(),
                skillExtensionIds
            );
            ExtensionCapabilitySnapshot? extensionCapabilities = acceptedExtensionCapabilities;
            if (extensionPins != null && extensionIds != null)
            {
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The execution extension selection is invalid."
                );
            }

            if (
                extensionCapabilities != null
                && extensionPins != null
                && !extensionCapabilities.Pins.SequenceEqual(extensionPins)
            )
            {
                throw new AgwException(ErrorCodes.ExtensionVersionMismatch);
            }

            if (extensionCapabilities == null && extensionPins is { Count: > 0 })
            {
                throw new AgwException(ErrorCodes.ExtensionUnavailable);
            }

            if (extensionCapabilities == null)
            {
                if (extensionIds != null)
                {
                    extensionCapabilities = await _extensionCapabilityCatalog
                        .AcquireForExecutionAsync(extensionIds, extensionSelection, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    extensionCapabilities = await _extensionCapabilityCatalog
                        .AcquireCurrentAsync(extensionSelection, cancellationToken)
                        .ConfigureAwait(false);
                }
                lease.Add(extensionCapabilities);
            }

            if (extensionCapabilities != null)
            {
                var selectedTools = extensionCapabilities.SelectTools(extensionSelection);
                AddTools(combined.Tools, registeredToolNames, selectedTools, "extension");
                combined.PlanModeAllowedToolNames.UnionWith(
                    selectedTools
                        .Where(tool => AgwToolMetadataBinding.GetMetadata(tool)?.AllowInPlanMode == true)
                        .Select(tool => tool.Name)
                );
            }

            // 静态 Provider 先绑定 Tool Block 成员，动态 Provider 再校验全部成员。
            // Static providers bind their Tool Block members before dynamic providers validate the complete set.
            combined.ContextProviders.Add(workspaceProvider);
            combined.ContextProviders.AddRange(combined.DynamicContextProviders);
            if (deferHumanInteractions)
                combined.ContextProviders.Add(new DeferredHumanInteractionProvider());

            return BuildComposition(extensionCapabilities?.Skills);
        }
        catch
        {
            await DisposeWithoutThrowingAsync(lease).ConfigureAwait(false);
            throw;
        }
    }

    private static int GetDefinitionOrder(string name, IReadOnlyList<string> names)
    {
        for (var index = 0; index < names.Count; index++)
        {
            if (string.Equals(name, names[index], StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return names.Count;
    }

    private async Task AddIndependentMcpToolsAsync(
        Agent agent,
        Project project,
        IReadOnlyDictionary<string, string> environmentVariables,
        ICollection<AITool> tools,
        ISet<string> registeredToolNames,
        AgentResourceLease lease,
        CancellationToken cancellationToken
    )
    {
        var serverIds = agent
            .AgentMcpToolServers.Select(relation => relation.McpToolServerId)
            .Concat(project.ProjectMcpToolServers.Select(relation => relation.McpToolServerId));
        var servers = await _agentAppService.ListEnabledMcpToolServersAsync(serverIds).ConfigureAwait(false);
        foreach (var server in servers)
        {
            try
            {
                var descriptor = CreateDescriptor(server);
                var materialized = await _mcpToolMaterializer
                    .MaterializeAsync(descriptor, new McpRuntimeOverrides(environmentVariables), cancellationToken)
                    .ConfigureAwait(false);
                lease.Add(materialized);
                AddTools(tools, registeredToolNames, materialized.Tools, $"mcp:{server.Id}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AgwException exception)
                when (exception.Code == ErrorCodes.IntegrationToolNameInvalid.Code
                    || exception.Code == ErrorCodes.IntegrationToolNameConflict.Code
                )
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to materialize independent MCP server {ServerId}", server.Id);
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
                server.EnvironmentVariables
            ),
            "http" => new McpHttpEndpointDescriptor(server.Name, CreateEndpoint(server.Url), server.Headers),
            "sse" => new McpSseEndpointDescriptor(server.Name, CreateEndpoint(server.Url), server.Headers),
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
        string source
    )
    {
        foreach (var tool in tools)
        {
            RegisterToolName(registeredToolNames, tool.Name, source);
            destination.Add(tool);
        }
    }

    private static void AddContribution(
        ToolContribution destination,
        ToolContribution contribution,
        ISet<string> registeredToolNames,
        string source,
        AgentResourceLease lease
    )
    {
        lease.Add(contribution);
        foreach (var tool in contribution.Tools)
            RegisterToolName(registeredToolNames, tool.Name, source);
        destination.Merge(contribution);
    }

    private static void RegisterToolName(ISet<string> registeredToolNames, string name, string source)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new AgwException(
                ErrorCodes.IntegrationToolNameInvalid,
                $"Capability source '{source}' produced a tool without a name."
            );
        if (!registeredToolNames.Add(name))
        {
            throw new AgwException(
                ErrorCodes.IntegrationToolNameConflict,
                $"Tool name '{name}' conflicts with another agent capability source."
            );
        }
    }

    private static async ValueTask DisposeWithoutThrowingAsync(IAsyncDisposable resource)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private static IReadOnlyList<ToolValueObject> SelectUserMemoryValues(IReadOnlyList<ToolValueObject>? values) =>
        values?.Where(static value => value is ToolBlockValue { Definition: UserMemoryToolBlockDefinition }).ToArray()
        ?? [];
}
