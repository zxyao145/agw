using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Middleware;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Agents.ExternalAgents.Pi;
using Agw.Files.Abstracts;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Registration;
using Agw.Skills.Contracts.Remote;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Agents;

public partial class AgentRuntimeService : IAgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectRuntimeFacade _projectRuntimeFacade;
    private readonly AgentCapabilityComposer _capabilityComposer;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly IProjectProviderSessionFacade _providerSessions;
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
    private readonly IRuntimeTurnContextAccessor? _turnContextAccessor;
    private readonly IProjectDefaultResolver? _projectDefaults;
    private readonly TimeProvider _timeProvider;
    private readonly PiExternalAgentOptions _piExternalAgentOptions;

    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;

    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectRuntimeFacade projectRuntimeFacade,
        AgentCapabilityComposer capabilityComposer,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        IProjectProviderSessionFacade providerSessions,
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
        IConversationHandoffProvider? conversationHandoffProvider = null,
        IRuntimeTurnContextAccessor? turnContextAccessor = null,
        TimeProvider? timeProvider = null,
        IProjectDefaultResolver? projectDefaults = null,
        IOptions<PiExternalAgentOptions>? piExternalAgentOptions = null
    )
    {
        _agentAppService = agentAppService;
        _projectRuntimeFacade = projectRuntimeFacade;
        _capabilityComposer = capabilityComposer;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _providerSessions = providerSessions;
        _dataPaths = dataPaths;
        _fileSystemResolver = fileSystemResolver;
        _sessionStateStore = sessionStateStore;
        _logger = logger;
        _observabilityMiddleware = observabilityMiddleware;
        _usageTrackingMiddleware = usageTrackingMiddleware;
        _summaryService = summaryService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _conversationHistoryWriter = conversationHistoryWriter ?? chatHistoryProvider as IConversationHistoryWriter;
        _skillRegistrations = (skillRegistrations ?? [])
            .GroupBy(registration => registration.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _remoteSkillContentResolver = remoteSkillContentResolver;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
        _conversationHandoffProvider = conversationHandoffProvider;
        _turnContextAccessor = turnContextAccessor;
        _projectDefaults = projectDefaults;
        _piExternalAgentOptions = piExternalAgentOptions?.Value ?? new PiExternalAgentOptions();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _services = services ?? new ServiceCollection().BuildServiceProvider();
    }

    private async Task<Guid?> ResolveProjectIdAsync(Guid? projectId, CancellationToken cancellationToken)
    {
        if (
            projectId.HasValue
            && projectId.Value != Guid.Empty
            && projectId.Value != ProjectDefaults.DefaultBuiltInId
            && projectId.Value != ProjectDefaults.A2AId
        )
        {
            return projectId.Value;
        }

        if (_projectDefaults == null)
        {
            return ProjectDefaults.DefaultBuiltInId;
        }

        return projectId == ProjectDefaults.A2AId
            ? await _projectDefaults.ResolveA2AProjectIdAsync(cancellationToken).ConfigureAwait(false)
            : await _projectDefaults.ResolveDefaultProjectIdAsync(cancellationToken).ConfigureAwait(false);
    }
}
