using ClaudeCodeSdk.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace DSystem.ExternalAgents;

/// <summary>
/// Manages a ClaudeCode session with agent and thread state.
/// </summary>
public sealed class ClaudeCodeSession : IAsyncDisposable
{
    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Gets the ClaudeCode AI Agent.
    /// </summary>
    public ClaudeCodeAIAgent Agent { get; }

    /// <summary>
    /// Gets the agent thread for conversation context.
    /// </summary>
    public AgentThread Thread { get; private set; } 

    /// <summary>
    /// Gets the session configuration.
    /// </summary>
    public ClaudeCodeSettingRequest Configuration { get; }

    /// <summary>
    /// Gets the cancellation token for in-flight requests.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the ClaudeCodeSession class.
    /// </summary>
    public ClaudeCodeSession(
        ClaudeCodeAIAgent agent,
        AgentThread thread,
        ClaudeCodeSettingRequest configuration,
        ILogger logger)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Updates the thread state (useful for thread deserialization).
    /// </summary>
    public void UpdateThread(AgentThread newThread) => Thread = newThread;

    /// <summary>
    /// Cancels any in-flight request.
    /// </summary>
    public void CancelActiveRequest()
    {
        if (_cancellationTokenSource.IsCancellationRequested) return;
        _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Prepares a new cancellation token for a subsequent request.
    /// </summary>
    public void ResetCancellationToken()
    {
        _cancellationTokenSource.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await Agent.DisposeAsync();
            _cancellationTokenSource.Dispose();
            _logger.LogDebug("ClaudeCodeSession disposed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disposing ClaudeCodeSession");
        }
        finally
        {
            _disposed = true;
        }
    }
}
