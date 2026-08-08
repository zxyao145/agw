using System.Runtime.CompilerServices;

using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Store;
using Agw.Agents.Execution.Agents.Tools;
using Agw.Agents.Execution.Summaries;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Runtimes;

public sealed class AgentRuntime : RuntimeBase
{
    private const int MaxToolApprovalRounds = 32;

    private bool _disposed;
    private CancellationTokenSource _cancellationTokenSource = new();

    public AIAgent Agent { get; }
    public AgentSession Session { get; private set; }
    public CancellationToken CancellationToken => _cancellationTokenSource.Token;

    private readonly ILogger _logger;
    private readonly bool _enableSummary;
    private readonly Guid? _summaryModelProviderId;
    private readonly IAgentTurnSummaryService? _summaryService;
    private readonly IConversationHistoryWriter? _conversationHistoryWriter;
    public AgentSessionStateScope? SessionStateScope { get; }
    public AgentType AgentType { get; }
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
        IConversationHistoryWriter? conversationHistoryWriter = null)
    {
        Agent = agent ?? throw new AgwException(ErrorCodes.InvalidParam, "agent cannot be null.");
        Session = thread ?? throw new AgwException(ErrorCodes.InvalidParam, "thread cannot be null.");
        _projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        _contextId = contextId;
        SessionStateScope = sessionStateScope;
        AgentType = agentType;
        _logger = logger ?? throw new AgwException(ErrorCodes.InvalidParam, "logger cannot be null.");
        _enableSummary = enableSummary;
        _summaryModelProviderId = summaryModelProviderId;
        _summaryService = summaryService;
        _conversationHistoryWriter = conversationHistoryWriter;
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
                           approvalHandler: null,
                           cancellationToken))
        {
            yield return message;
        }
    }

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgwUserInput input,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(input, approvalHandler: null, cancellationToken);
    }

    public async Task<IReadOnlyList<AgwMessage>> ExecuteAsync(
        AgwUserInput input,
        IHumanGateApprovalHandler? approvalHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Contents);

        var message = new ChatMessage(ChatRole.User, ConvertToAIContents(input.Contents))
        {
            MessageId = string.IsNullOrWhiteSpace(input.MessageId) ? Guid.CreateVersion7().ToString() : input.MessageId,
            AuthorName = string.IsNullOrWhiteSpace(input.Author) ? Constants.DefaultInputAuthor : input.Author,
        };
        var turnPersistence = new ToolTurnPersistence(
            Agent,
            Session,
            PersistToolBlockMessagesAsync);
        Exception? executionFailure = null;
        try
        {
            var requestMessage = message;
            var approvalPending = false;
            for (var approvalCount = 0; approvalCount < MaxToolApprovalRounds; approvalCount++)
            {
                var response = await Agent.RunAsync(
                    requestMessage,
                    Session,
                    cancellationToken: cancellationToken);
                turnPersistence.RecordRange(response.Messages);
                var approvals = response.Messages
                    .SelectMany(static item => item.Contents)
                    .OfType<ToolApprovalRequestContent>()
                    .ToList();
                if (approvals.Count == 0)
                {
                    approvalPending = false;
                    break;
                }

                approvalPending = true;
                if (approvalHandler == null)
                {
                    throw new AgwException(
                        ErrorCodes.AgentExecutionFailed,
                        "Tool approval requires an active interactive approval channel.");
                }

                var approvalResponses = new List<AIContent>(approvals.Count);
                foreach (var approval in approvals)
                {
                    var request = ToolApprovalSupport.CreateRequest(approval, "standalone", Agent.Name);
                    var decision = await approvalHandler.WaitForApprovalAsync(request, cancellationToken);
                    approvalResponses.Add(ToolApprovalSupport.CreateResponse(approval, decision));
                }

                requestMessage = new ChatMessage(ChatRole.User, approvalResponses);
            }

            ThrowIfApprovalLimitExceeded(approvalPending);

            var messages = turnPersistence.ResponseMessages
                .Select(item => item.ToAiMessage())
                .OfType<AgwMessage>()
                .ToList();
            var stateSnapshots = await turnPersistence
                .CompleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
            messages.AddRange(
                stateSnapshots
                    .Select(static stateMessage => stateMessage.ToAiMessage())
                    .OfType<AgwMessage>());
            var result = await CreateSummaryAsync(
                    input,
                    turnPersistence.ResponseMessages,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result != null)
            {
                messages.Add(result);
            }

            return messages;
        }
        catch (Exception exception)
        {
            executionFailure = exception;
            throw;
        }
        finally
        {
            if (!turnPersistence.CompletionAttempted)
            {
                try
                {
                    await turnPersistence
                        .CompleteAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception persistenceException) when (executionFailure != null)
                {
                    _logger.LogError(
                        persistenceException,
                        "Failed to persist Tool state while preserving an Agent execution failure.");
                }
            }
        }
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgwUserInput input,
        IHumanGateApprovalHandler? approvalHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Contents);

        await foreach (var message in ExecuteStreamingAsync(
                           input.Contents,
                           input.MessageId,
                           input.Author,
                           approvalHandler,
                           cancellationToken))
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        List<AgwContent> contents,
        string? messageId,
        string? author,
        IHumanGateApprovalHandler? approvalHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var aiContents = ConvertToAIContents(contents);
        var message = new ChatMessage(ChatRole.User, aiContents)
        {
            MessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.CreateVersion7().ToString() : messageId,
            AuthorName = string.IsNullOrWhiteSpace(author) ? Constants.DefaultInputAuthor : author
        };

        var assistantText = new List<string>();
        var turnPersistence = new ToolTurnPersistence(
            Agent,
            Session,
            PersistToolBlockMessagesAsync);
        try
        {
            ChatMessage requestMessage = message;
            var approvalPending = false;
            for (var approvalCount = 0; approvalCount < MaxToolApprovalRounds; approvalCount++)
            {
                var approvals = new List<ToolApprovalRequestContent>();
                await foreach (var update in Agent.RunStreamingAsync(
                                   requestMessage,
                                   Session,
                                   cancellationToken: cancellationToken))
                {
                    turnPersistence.Record(ToolStateSnapshots.ToMessage(update));
                    assistantText.AddRange(
                        update.Contents
                            .OfType<TextContent>()
                            .Select(content => content.Text));
                    approvals.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());

                    var aiMessage = update.ToAiMessage();
                    if (aiMessage != null && aiMessage.Contents.Count > 0)
                    {
                        yield return aiMessage;
                    }
                }

                if (approvals.Count == 0)
                {
                    approvalPending = false;
                    break;
                }

                approvalPending = true;
                if (approvalHandler == null)
                {
                    throw new AgwException(
                        ErrorCodes.AgentExecutionFailed,
                        "Tool approval requires an active interactive approval channel.");
                }

                var approvalResponses = new List<AIContent>(approvals.Count);
                foreach (var approval in approvals)
                {
                    var request = ToolApprovalSupport.CreateRequest(approval, "standalone", Agent.Name);
                    if (approvalHandler.RequiresHumanResponse(request))
                    {
                        yield return ToolApprovalSupport.CreateMessage(request);
                    }

                    var decision = await approvalHandler.WaitForApprovalAsync(request, cancellationToken);
                    approvalResponses.Add(ToolApprovalSupport.CreateResponse(approval, decision));
                }

                requestMessage = new ChatMessage(ChatRole.User, approvalResponses);
            }

            ThrowIfApprovalLimitExceeded(approvalPending);

            var stateSnapshots = await turnPersistence
                .CompleteAsync(CancellationToken.None)
                .ConfigureAwait(false);
            foreach (var stateSnapshot in stateSnapshots)
            {
                if (stateSnapshot.ToAiMessage() is { } stateMessage)
                {
                    yield return stateMessage;
                }
            }

            var result = await CreateSummaryAsync(
                input: new AgwUserInput { MessageId = message.MessageId!, Author = message.AuthorName, Contents = contents },
                assistantMessages: [new ChatMessage(ChatRole.Assistant, string.Concat(assistantText))],
                cancellationToken)
                .ConfigureAwait(false);
            if (result != null)
            {
                yield return result;
            }

            _logger.LogDebug("Saved thread state for context: {ContextId}", _contextId);
        }
        finally
        {
            if (!turnPersistence.CompletionAttempted)
            {
                try
                {
                    await turnPersistence
                        .CompleteAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Failed to persist Tool state while finalizing an Agent stream.");
                }
            }
        }
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

    private static void ThrowIfApprovalLimitExceeded(bool approvalPending)
    {
        if (approvalPending)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                $"Tool approval exceeded the limit of {MaxToolApprovalRounds} rounds.");
        }
    }

    private Task PersistToolBlockMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        return _conversationHistoryWriter == null || messages.Count == 0
            ? Task.CompletedTask
            : _conversationHistoryWriter.AppendAsync(
                _projectId,
                _contextId,
                messages,
                cancellationToken);
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

    private async Task<AgwMessage?> CreateSummaryAsync(
        AgwUserInput input,
        IReadOnlyList<ChatMessage> assistantMessages,
        CancellationToken cancellationToken)
    {
        if (!_enableSummary || !_summaryModelProviderId.HasValue || _summaryService == null)
        {
            return null;
        }

        var userText = string.Concat(
                input.Contents
                    .OfType<AgwTextContent>()
                    .Select(content => content.Content))
            .Trim();
        var sourceMessages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(userText))
        {
            sourceMessages.Add(new ChatMessage(ChatRole.User, userText));
        }

        var assistantText = string.Concat(
                assistantMessages
                    .Where(message => message.Role == ChatRole.Assistant)
                    .SelectMany(message => message.Contents)
                    .OfType<TextContent>()
                    .Select(content => content.Text))
            .Trim();
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            sourceMessages.Add(new ChatMessage(ChatRole.Assistant, assistantText));
        }

        var result = await _summaryService.CreateResultAsync(
            _summaryModelProviderId.Value,
            sourceMessages,
            _projectId,
            _contextId,
            customInstructions: null,
            cancellationToken)
            .ConfigureAwait(false);
        return result.ToAiMessage();
    }
}
