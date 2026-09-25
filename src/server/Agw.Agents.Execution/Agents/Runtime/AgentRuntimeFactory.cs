using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Agents.History;
using Agw.Agents.Execution.Agents.Middleware.Telemetry;
using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.Summaries;
using Agw.Files.Abstracts;
using Agw.Projects.Contracts.History;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Runtime;
using Agw.Skills.Contracts.Registration;
using Agw.Skills.Contracts.Remote;
using Agw.Tools.Generated;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// 从 Agent Definition 与其 Engine 构造 AgentRuntime，也构造 Agentflow 节点与后台任务使用的 Agent。
/// Builds AgentRuntimes from an Agent definition and its Engine, as well as the Agents used by Agentflow nodes and background tasks.
/// </summary>
public partial class AgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly ILogger<AgentRuntimeFactory> _logger;
    private readonly AgentAppService _agentAppService;
    private readonly IProjectRuntimeFacade _projectRuntimeFacade;
    private readonly AgentCapabilityComposer _capabilityComposer;
    private readonly IConversationHistoryStore _historyStore;
    private readonly HistorySessionState _historySession;
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
    private readonly AgentRuntimeConfiguration _configuration;
    private readonly IAgentExecutionContextAccessor? _executionContext;
    private readonly IProjectDefaultResolver _projectDefaults;
    private readonly TimeProvider _timeProvider;
    private readonly AgwGeneratedToolCatalog? _generatedToolCatalog;

    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ExternalAgents.ClaudeCode.ClaudeToolApprovalCache _claudeApprovals = new();

    public AgentRuntimeFactory(
        AgentAppService agentAppService,
        IProjectRuntimeFacade projectRuntimeFacade,
        AgentCapabilityComposer capabilityComposer,
        IConversationHistoryStore historyStore,
        HistorySessionState historySession,
        ExternalProviderSessionBindings providerBindings,
        AgwDataPaths dataPaths,
        IAgwFileSystemResolver fileSystemResolver,
        AgentSessionStateStore sessionStateStore,
        ILogger<AgentRuntimeFactory> logger,
        AgentTelemetryMiddleware telemetryMiddleware,
        IAgentTurnSummaryService summaryService,
        IServiceProvider services,
        IProjectDefaultResolver projectDefaults,
        AgentRuntimeConfiguration configuration,
        IEnumerable<IAgentSkillRegistration> skillRegistrations,
        IRemoteSkillContentResolver? remoteSkillContentResolver,
        ILoggerFactory loggerFactory,
        IConversationHistoryWriter? conversationHistoryWriter,
        HumanInteractionContextAccessor? humanInteractionContextAccessor,
        IAgentExecutionContextAccessor? executionContext,
        TimeProvider timeProvider,
        AgwGeneratedToolCatalog? generatedToolCatalog
    )
    {
        _agentAppService = agentAppService;
        _projectRuntimeFacade = projectRuntimeFacade;
        _capabilityComposer = capabilityComposer;
        _historyStore = historyStore;
        _historySession = historySession;
        _providerSessionState = historySession;
        _providerBindings = providerBindings;
        _dataPaths = dataPaths;
        _fileSystemResolver = fileSystemResolver;
        _sessionStateStore = sessionStateStore;
        _logger = logger;
        _telemetryMiddleware = telemetryMiddleware;
        _summaryService = summaryService;
        _timeProvider = timeProvider;
        _conversationHistoryWriter = conversationHistoryWriter;
        _skillRegistrations = skillRegistrations
            .GroupBy(registration => registration.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _remoteSkillContentResolver = remoteSkillContentResolver;
        _humanInteractionContextAccessor = humanInteractionContextAccessor;
        _configuration = configuration;
        _executionContext = executionContext;
        _projectDefaults = projectDefaults;
        _generatedToolCatalog = generatedToolCatalog;
        _loggerFactory = loggerFactory;
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(projectDefaults);
        _services = services;
    }

    internal AgwChatHistoryProvider CreateHistoryProvider(EngineKind engine, bool structuredResult) =>
        AgwChatHistoryProvider.Create(_historyStore, _historySession, _timeProvider, engine, structuredResult);

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
