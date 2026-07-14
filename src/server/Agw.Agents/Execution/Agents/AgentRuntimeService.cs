using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Summaries;
using Agw.Domain.Services;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Runtime;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService : IAgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectAppService _projectAppService;
    private readonly ToolRegistryService _toolRegistry;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly ITaskSessionBindingService _taskSessionBindingService;
    private readonly AgwDataPaths _dataPaths;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly AgentSessionStateStore _sessionStateStore;
    private readonly ObservabilityMiddleware _observabilityMiddleware;
    private readonly UsageTrackingMiddleware _usageTrackingMiddleware;
    private readonly IAgentTurnSummaryService _summaryService;
    private readonly Func<
        McpServer,
        IReadOnlyDictionary<string, string>,
        CancellationToken,
        Task<IReadOnlyList<AITool>>> _mcpToolLister;

    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        ITaskSessionBindingService taskSessionBindingService,
        AgwDataPaths dataPaths,
        IAgwFileSystemResolver fileSystemResolver,
        AgentSessionStateStore sessionStateStore,
        ILogger<AgentRuntimeService> logger,
        ObservabilityMiddleware observabilityMiddleware,
        UsageTrackingMiddleware usageTrackingMiddleware,
        IAgentTurnSummaryService summaryService)
        : this(
            agentAppService,
            projectAppService,
            toolRegistry,
            chatHistoryProvider,
            providerSessionState,
            taskSessionBindingService,
            dataPaths,
            fileSystemResolver,
            sessionStateStore,
            logger,
            observabilityMiddleware,
            usageTrackingMiddleware,
            summaryService,
            ListMcpToolsAsync)
    {
    }

    internal AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectAppService projectAppService,
        ToolRegistryService toolRegistry,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        ITaskSessionBindingService taskSessionBindingService,
        AgwDataPaths dataPaths,
        IAgwFileSystemResolver fileSystemResolver,
        AgentSessionStateStore sessionStateStore,
        ILogger<AgentRuntimeService> logger,
        ObservabilityMiddleware observabilityMiddleware,
        UsageTrackingMiddleware usageTrackingMiddleware,
        IAgentTurnSummaryService summaryService,
        Func<
            McpServer,
            IReadOnlyDictionary<string, string>,
            CancellationToken,
            Task<IReadOnlyList<AITool>>> mcpToolLister)
    {
        _agentAppService = agentAppService;
        _projectAppService = projectAppService;
        _toolRegistry = toolRegistry;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _taskSessionBindingService = taskSessionBindingService;
        _dataPaths = dataPaths;
        _fileSystemResolver = fileSystemResolver;
        _sessionStateStore = sessionStateStore;
        _logger = logger;
        _observabilityMiddleware = observabilityMiddleware;
        _usageTrackingMiddleware = usageTrackingMiddleware;
        _summaryService = summaryService;
        _mcpToolLister = mcpToolLister;
    }
}
