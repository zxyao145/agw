using System.Runtime.CompilerServices;

using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Models;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Application;

public sealed class AgentExecSession : IAsyncDisposable
{
    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    public AIAgent Agent { get; }
    public AgentSession Session { get; private set; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;
    public readonly string _taskId;
    public readonly Guid _projectId;
    public readonly string _contextId;
    private readonly AgentRuntimeType _agentType;
    private readonly Guid? _agentId;
    private readonly string? _agentName;
    private readonly string? _taskTitle;
    private readonly string? _taskDescription;
    private readonly string? _systemPrompt;

    public AgentExecSession(
        AIAgent agent,
        AgentSession thread,
        Guid projectId,
        string contextId,
        string? taskId,
        AgentRuntimeType agentType,
        Guid? agentId,
        string? agentName,
        ILogger logger,
        string? taskTitle = null,
        string? taskDescription = null,
        string? systemPrompt = null)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Session = thread ?? throw new ArgumentNullException(nameof(thread));
        _projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        _contextId = contextId;
        _taskId = taskId ?? Guid.NewGuid().ToString();
        _agentType = agentType;
        _agentId = agentId;
        _agentName = agentName;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskTitle = taskTitle;
        _taskDescription = taskDescription;
        _systemPrompt = systemPrompt;
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        string input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var content = new AgwTextContent
        {
            Content = input,
        };

        await foreach (var message in ExecuteStreamingAsync(
                           new AgwUserInput
                           {
                               Author = Constants.DefaultAuthor,
                               Contents = [content]
                           },
                           cancellationToken))
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Contents);

        await foreach (var message in ExecuteStreamingAsync(
                           input.Contents,
                           input.MessageId,
                           input.Author,
                           cancellationToken))
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        List<AgwContent> contents,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in ExecuteStreamingAsync(contents, null, null, cancellationToken))
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        List<AgwContent> contents,
        string? messageId,
        string? author,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiContents = ConvertToAIContents(contents);
        var message = new ChatMessage(ChatRole.User, aiContents)
        {
            MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString() : messageId,
            AuthorName = string.IsNullOrWhiteSpace(author) ? Constants.DefaultAuthor : author
        };

        await foreach (var update in Agent.RunStreamingAsync(message, Session, cancellationToken: cancellationToken))
        {
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null)
            {
                yield return aiMessage;
            }
        }

        _logger.LogDebug("Saved thread state for task: {TaskId}", _taskId);
    }

    public void UpdateThread(AgentSession newThread) => Session = newThread;

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

    private static List<AIContent> ConvertToAIContents(List<AgwContent> contents)
    {
        var aiContents = new List<AIContent>();

        foreach (var item in contents)
        {
            switch (item)
            {
                case AgwTextContent text:
                    aiContents.Add(new TextContent(text.Content)
                    {
                        AdditionalProperties = CloneAdditionalProperties(text.AdditionalProperties)
                    });
                    break;

                case AgwUriContent uri:
                    aiContents.Add(new UriContent(uri.Uri, uri.MediaType)
                    {
                        AdditionalProperties = CloneAdditionalProperties(uri.AdditionalProperties)
                    });
                    break;
            }
        }

        return aiContents;
    }

    private static AdditionalPropertiesDictionary? CloneAdditionalProperties(
        AdditionalPropertiesDictionary? additionalProperties) =>
        additionalProperties == null
            ? null
            : new AdditionalPropertiesDictionary(additionalProperties);
}
