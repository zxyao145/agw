using Agw.Appliaction.Services;
using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Agw.Appliaction;

/// <summary>
/// Wraps an AI agent to execute WebSocket inputs and build streaming outputs.
/// </summary>
public sealed class AgentExecSession : IAsyncDisposable
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
    /// Gets the cancellation token for in-flight requests.
    /// </summary>
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;
    public readonly string _sessionId;
    public readonly string _projectId;
    public readonly string _contextId;

    private readonly ProjectTaskAgentType _agentType;
    private readonly Guid? _agentId;
    private readonly string? _agentName;
    private readonly string? _taskTitle;
    private readonly string? _taskDescription;
    private readonly string? _systemPrompt;

    /// <summary>
    /// Initializes a new instance of the AiAgentSession class.
    /// </summary>
    public AgentExecSession(
        AIAgent agent,
        AgentSession thread,
        string projectId,
        string contextId,
        string? sessionId,
        ProjectTaskAgentType agentType,
        Guid? agentId,
        string? agentName,
        ILogger logger,
        string? taskTitle = null,
        string? taskDescription = null,
        string? systemPrompt = null)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Session = thread ?? throw new ArgumentNullException(nameof(thread));
        _projectId = projectId;
        _contextId = contextId;
        _sessionId = sessionId ?? Guid.NewGuid().ToString();
        _agentType = agentType;
        _agentId = agentId;
        _agentName = agentName;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskTitle = taskTitle;
        _taskDescription = taskDescription;
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// Executes a text input with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var content = new AgwTextContent()
        { 
            Content = input,
        };

        await foreach (var message in ExecuteStreamingAsync([content], input, cancellationToken))
        {
            yield return message;
        }
    }

    /// <summary>
    /// Executes a list of input contents with streaming responses.
    /// </summary>
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        List<AgwContent> contents,
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
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        List<AgwContent> contents,
        string? input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiContents = ConvertToAIContents(contents);
        var message = new ChatMessage(ChatRole.User, aiContents)
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = Constants.DefaultAuthor
        };
        var responseUpdates = new List<AgentResponseUpdate>();

        await foreach (var update in Agent.RunStreamingAsync(message, Session, cancellationToken: cancellationToken))
        {
            responseUpdates.Add(update);
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null) yield return aiMessage;
        }
        _logger.LogDebug("Saved thread state for session: {SessionId}", _sessionId);
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

    private static List<AIContent> ConvertToAIContents(List<AgwContent> contents)
    {
        var aiContents = new List<AIContent>();

        foreach (var item in contents)
        {
            if (item is AgwTextContent agwTextContent)
            {
                aiContents.Add(new TextContent(agwTextContent.Content));
                continue;
            }
            if (item is AgwUriContent agwUriContent)
            {
                aiContents.Add(new UriContent(agwUriContent.Uri, agwUriContent.MediaType));
                continue;
            }
        }

        return aiContents;
    }
}
