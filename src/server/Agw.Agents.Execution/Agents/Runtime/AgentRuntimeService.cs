using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Registration;
using Agw.Skills.Contracts.Remote;
using Agw.Tools.Generated;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Agents.Execution.Agents.Runtime;

public partial class AgentRuntimeService : IAgentRuntimeService
{
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectRuntimeFacade _projectRuntimeFacade;
    private readonly AgentCapabilityComposer _capabilityComposer;
    private readonly ChatHistoryProvider _chatHistoryProvider;
    private readonly IProviderSessionState _providerSessionState;
    private readonly ExternalProviderSessionBindings _providerBindings;
    private readonly AgwDataPaths _dataPaths;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly AgentSessionStateStore _sessionStateStore;
    private readonly AgentTelemetryMiddleware _telemetryMiddleware;
    private readonly IAgentTurnSummaryService _summaryService;
    private readonly IConversationHistoryWriter? _conversationHistoryWriter;
    private readonly IReadOnlyDictionary<Guid, IAgentSkillRegistration> _skillRegistrations;
    private readonly IRemoteSkillContentResolver? _remoteSkillContentResolver;
    private readonly HumanInteractionContextAccessor? _humanInteractionContextAccessor;
    private readonly AgentTurnExecutor _turnExecutor;
    private readonly AgentRuntimeConfiguration _configuration;
    private readonly IRuntimeTurnContextAccessor? _turnContextAccessor;
    private readonly IProjectDefaultResolver _projectDefaults;
    private readonly TimeProvider _timeProvider;
    private readonly AgwGeneratedToolCatalog? _generatedToolCatalog;

    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ExternalAgents.ClaudeCode.ClaudeToolApprovalCache _claudeApprovals = new();

    public AgentRuntimeService(
        AgentAppService agentAppService,
        IProjectRuntimeFacade projectRuntimeFacade,
        AgentCapabilityComposer capabilityComposer,
        ChatHistoryProvider chatHistoryProvider,
        IProviderSessionState providerSessionState,
        ExternalProviderSessionBindings providerBindings,
        AgwDataPaths dataPaths,
        IAgwFileSystemResolver fileSystemResolver,
        AgentSessionStateStore sessionStateStore,
        ILogger<AgentRuntimeService> logger,
        AgentTelemetryMiddleware telemetryMiddleware,
        IAgentTurnSummaryService summaryService,
        IServiceProvider services,
        IProjectDefaultResolver projectDefaults,
        AgentTurnExecutor turnExecutor,
        AgentRuntimeConfiguration configuration,
        IEnumerable<IAgentSkillRegistration>? skillRegistrations = null,
        IRemoteSkillContentResolver? remoteSkillContentResolver = null,
        ILoggerFactory? loggerFactory = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        HumanInteractionContextAccessor? humanInteractionContextAccessor = null,
        IRuntimeTurnContextAccessor? turnContextAccessor = null,
        TimeProvider? timeProvider = null,
        AgwGeneratedToolCatalog? generatedToolCatalog = null
    )
    {
        _agentAppService = agentAppService;
        _projectRuntimeFacade = projectRuntimeFacade;
        _capabilityComposer = capabilityComposer;
        _chatHistoryProvider = chatHistoryProvider;
        _providerSessionState = providerSessionState;
        _providerBindings = providerBindings;
        _dataPaths = dataPaths;
        _fileSystemResolver = fileSystemResolver;
        _sessionStateStore = sessionStateStore;
        _logger = logger;
        _telemetryMiddleware = telemetryMiddleware;
        _summaryService = summaryService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _conversationHistoryWriter = conversationHistoryWriter ?? chatHistoryProvider as IConversationHistoryWriter;
        _skillRegistrations = (skillRegistrations ?? [])
            .GroupBy(registration => registration.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _remoteSkillContentResolver = remoteSkillContentResolver;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
        _turnExecutor = turnExecutor;
        _configuration = configuration;
        _turnContextAccessor = turnContextAccessor;
        _projectDefaults = projectDefaults;
        _generatedToolCatalog = generatedToolCatalog;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(projectDefaults);
        _services = services;
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

        return projectId == ProjectDefaults.A2AId
            ? await _projectDefaults.ResolveA2AProjectIdAsync(cancellationToken).ConfigureAwait(false)
            : await _projectDefaults.ResolveDefaultProjectIdAsync(cancellationToken).ConfigureAwait(false);
    }
}
