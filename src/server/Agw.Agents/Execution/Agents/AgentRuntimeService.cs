using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Summaries;
using Agw.Files.Abstracts;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Runtime;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService : IAgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectAppService _projectAppService;
    private readonly AgentCapabilityComposer _capabilityComposer;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly ITaskSessionBindingService _taskSessionBindingService;
    private readonly AgwDataPaths _dataPaths;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly AgentSessionStateStore _sessionStateStore;
    private readonly ObservabilityMiddleware _observabilityMiddleware;
    private readonly UsageTrackingMiddleware _usageTrackingMiddleware;
    private readonly IAgentTurnSummaryService _summaryService;
    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectAppService projectAppService,
        AgentCapabilityComposer capabilityComposer,
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
    {
        _agentAppService = agentAppService;
        _projectAppService = projectAppService;
        _capabilityComposer = capabilityComposer;
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
    }
}
