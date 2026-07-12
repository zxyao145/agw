using System.Runtime.CompilerServices;

using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Agw.Agents.Runtime.Execution;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Runtime.AgentRun.Dtos;

public sealed class AgentExecSession : RuntimeExecSessionBase
{
    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    public AIAgent Agent { get; }
    public AgentSession Session { get; private set; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;
    public readonly string SessionKey;
    public readonly Guid _projectId;
    public readonly string _contextId;

    public AgentExecSession(
        ILogger logger,
        AIAgent agent,
        AgentSession thread,
        Guid projectId,
        string contextId,
        string? sessionKey)
    {
        Agent = agent ?? throw new AgwException(ErrorCodes.InvalidParam, "agent cannot be null.");
        Session = thread ?? throw new AgwException(ErrorCodes.InvalidParam, "thread cannot be null.");
        _projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        _contextId = contextId;
        SessionKey = string.IsNullOrWhiteSpace(sessionKey) ? _contextId : sessionKey;
        _logger = logger ?? throw new AgwException(ErrorCodes.InvalidParam, "logger cannot be null.");
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

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgwUserInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Contents);

        var message = new ChatMessage(ChatRole.User, ConvertToAIContents(input.Contents))
        {
            MessageId = string.IsNullOrWhiteSpace(input.MessageId) ? Guid.NewGuid().ToString() : input.MessageId,
            AuthorName = string.IsNullOrWhiteSpace(input.Author) ? Constants.DefaultInputAuthor : input.Author,
        };
        var response = await Agent.RunAsync(message, Session, cancellationToken: cancellationToken);
        return response.Messages
            .Select(item => item.ToAiMessage())
            .OfType<AgwMessage>()
            .ToArray();
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
            AuthorName = string.IsNullOrWhiteSpace(author) ? Constants.DefaultInputAuthor : author
        };

        await foreach (var update in Agent.RunStreamingAsync(message, Session, cancellationToken: cancellationToken))
        {
            var aiMessage = update.ToAiMessage();
            if (aiMessage != null)
            {
                yield return aiMessage;
            }
        }

        _logger.LogDebug("Saved thread state for context: {ContextId}", _contextId);
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

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await base.DisposeAsync();
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
