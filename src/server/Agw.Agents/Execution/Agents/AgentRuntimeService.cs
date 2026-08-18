using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Runtime;
using Agw.Skills.Application.Remote;
using Agw.Skills.Contracts.Registration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly IConversationHistoryWriter? _conversationHistoryWriter;
    private readonly IReadOnlyDictionary<Guid, IAgentSkillRegistration> _skillRegistrations;
    private readonly IRemoteSkillContentResolver? _remoteSkillContentResolver;
    private readonly HumanInteractionContextAccessor? _humanInteractionContextAccessor;
    private readonly IConversationHandoffProvider? _conversationHandoffProvider;

    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;

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
        IAgentTurnSummaryService summaryService,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null,
        IRemoteSkillContentResolver? remoteSkillContentResolver = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        HumanInteractionContextAccessor? humanInteractionContextAccessor = null,
        IConversationHandoffProvider? conversationHandoffProvider = null
    )
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
        _conversationHistoryWriter = conversationHistoryWriter ?? chatHistoryProvider as IConversationHistoryWriter;
        _skillRegistrations = (skillRegistrations ?? [])
            .GroupBy(registration => registration.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _remoteSkillContentResolver = remoteSkillContentResolver;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
        _conversationHandoffProvider = conversationHandoffProvider;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _services = services ?? new ServiceCollection().BuildServiceProvider();
    }
}
