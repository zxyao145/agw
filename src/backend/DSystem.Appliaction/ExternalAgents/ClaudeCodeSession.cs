using DSystem.SessionRecords.Application;
using DSystem.Shared;
using DSystem.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DSystem.Appliaction.ExternalAgents;

/// <summary>
/// Wraps an AI agent to execute WebSocket inputs and build streaming outputs.
/// </summary>
public sealed class ClaudeCodeSession : IAsyncDisposable
{
    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Gets the AI agent.
    /// </summary>
    public AIAgent Agent { get; }

    /// <summary>
    /// Gets the agent thread for conversation context.
    /// </summary>
    public AgentSession Session { get; private set; } 

    /// <summary>
    /// Gets the session configuration.
    /// </summary>
    public ClaudeCodeSettingRequest Configuration { get; }

    /// <summary>
    /// Gets the cancellation token for in-flight requests.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;
    private readonly SessionRecordApplication _sessionRecordApplication;

    private const string ClaudeCodeAgentName = "ClaudeCode";

    /// <summary>
    /// Initializes a new instance of the ClaudeCodeSession class.
    /// </summary>
    public ClaudeCodeSession(
        AIAgent agent,
        AgentSession thread,
        ClaudeCodeSettingRequest configuration,
        ILogger logger,
        SessionRecordApplication sessionRecordApplication)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Session = thread ?? throw new ArgumentNullException(nameof(thread));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionRecordApplication = sessionRecordApplication ?? throw new ArgumentNullException(nameof(sessionRecordApplication));
    }

    /// <summary>
    /// Executes a text input with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var content = new AiMessageInputContent(
            AiMessageContentType.TextContent,
            JsonSerializer.SerializeToElement(input));

        await foreach (var message in ExecuteStreamingAsync([content], input, cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Executes a list of input contents with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        List<AiMessageInputContent> contents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in ExecuteStreamingAsync(contents, null, cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Executes a list of input contents with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        List<AiMessageInputContent> contents,
        string? input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiContents = ConvertToAIContents(contents);
        var message = new ChatMessage(ChatRole.User, aiContents);
        var responseUpdates = new List<AgentResponseUpdate>();

        await foreach (var update in Agent.RunStreamingAsync(message, Session, cancellationToken: cancellationToken))
        {
            responseUpdates.Add(update);
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null) yield return aiMessage;
        }

        await _sessionRecordApplication.SaveThreadStateAsync(
            Configuration.SessionId,
            Configuration.ProjectId,
            Session.Serialize(),
            responseUpdates,
            input,
            cancellationToken);
        _logger.LogDebug("Saved thread state for session: {ThreadId}", Configuration.SessionId);
    }

    /// <summary>
    /// Updates the thread state (useful for thread deserialization).
    /// </summary>
    public void UpdateThread(AgentSession newThread) => Session = newThread;

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

    private static List<AIContent> ConvertToAIContents(List<AiMessageInputContent> contents)
    {
        var aiContents = new List<AIContent>();

        foreach (var item in contents)
        {
            if (item.Type == AiMessageContentType.TextContent)
            {
                aiContents.Add(new TextContent(item.Content.GetString()));
                continue;
            }
            if (item.Type == AiMessageContentType.UriContent)
            {
                var uri = item.Content.GetProperty("uri").GetString() ?? "";
                var mediaType = item.Content.GetProperty("mediaType").GetString() ?? "";
                aiContents.Add(new UriContent(uri, mediaType));
            }
        }

        return aiContents;
    }

}
