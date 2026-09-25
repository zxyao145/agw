using Agw.Agents.Execution.Agents.Sessions;
using Agw.Agents.Execution.Summaries;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.Runtime;

/// <summary>
/// 按一个 Agent Definition 构造、绑定到某个对话的运行实例：AIAgent、AgentSession 与生成 Result 所需的配置。
/// The running instance built from one Agent definition and bound to a conversation: the AIAgent, its AgentSession and the Result configuration.
/// </summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly ILogger _logger;
    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    public AIAgent Agent { get; }
    public AgentSession Session { get; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;
    public AgentSessionStateScope? SessionStateScope { get; }
    public AgentType AgentType { get; }

    internal bool EnableSummary { get; }
    internal bool UseStructuredResult { get; }
    internal Guid? SummaryModelProviderId { get; }
    internal IAgentTurnSummaryService? SummaryService { get; }
    internal IConversationHistoryWriter? ConversationHistoryWriter { get; }

    /// <summary>
    /// 本轮结束时是否会产生 result 消息，决定 ResultOnly 能否生效。
    /// Whether the turn produces a result message, which decides if ResultOnly can take effect.
    /// </summary>
    internal bool EmitsTurnResult =>
        AgentType == AgentType.External || (AgentType == AgentType.System && EnableSummary);

    internal string? ConfigurationVersion { get; init; }
    internal bool IsDisposed => _disposed;
    public readonly Guid _projectId;
    public readonly string _contextId;

    public AgentRuntime(
        ILogger logger,
        AIAgent agent,
        AgentSession thread,
        Guid projectId,
        string contextId,
        AgentSessionStateScope? sessionStateScope,
        AgentType agentType = AgentType.System,
        bool enableSummary = false,
        Guid? summaryModelProviderId = null,
        IAgentTurnSummaryService? summaryService = null,
        IConversationHistoryWriter? conversationHistoryWriter = null,
        bool useStructuredResult = false
    )
    {
        Agent = agent ?? throw new AgwException(ErrorCodes.InvalidParam, "agent cannot be null.");
        Session = thread ?? throw new AgwException(ErrorCodes.InvalidParam, "thread cannot be null.");
        _projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        _contextId = contextId;
        SessionStateScope = sessionStateScope;
        AgentType = agentType;
        _logger = logger ?? throw new AgwException(ErrorCodes.InvalidParam, "logger cannot be null.");
        EnableSummary = enableSummary;
        UseStructuredResult = useStructuredResult;
        SummaryModelProviderId = summaryModelProviderId;
        SummaryService = summaryService;
        ConversationHistoryWriter = conversationHistoryWriter;
    }

    public void CancelActiveRequest()
    {
        if (_cancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
    }

    public void ResetCancellationToken()
    {
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (Agent is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (Agent is IDisposable disposable)
            {
                disposable.Dispose();
            }

            _cancellationTokenSource.Dispose();
            _logger.LogDebug("AiAgentSession disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing AiAgentSession");
        }
        finally
        {
            _disposed = true;
        }
    }
}
