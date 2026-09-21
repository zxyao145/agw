using System.Collections.Concurrent;
using System.Text;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.History;

/// <summary>Owns protocol projections while delegating input history and persistence to Projects.</summary>
internal sealed class NormalizedChatHistoryProvider
    : ChatHistoryProvider,
        IStreamingConversationHistoryProvider,
        IConversationHistoryRequests
{
    private readonly ChatHistoryProvider _inner;
    private readonly TimeProvider _timeProvider;
    private readonly Agw.Agents.Execution.Turns.IRuntimeTurnContextAccessor? _turnContext;
    private readonly ConcurrentDictionary<AgentSession, Capture> _captures = new(ReferenceEqualityComparer.Instance);

    internal NormalizedChatHistoryProvider(
        ChatHistoryProvider inner,
        TimeProvider timeProvider,
        Agw.Agents.Execution.Turns.IRuntimeTurnContextAccessor? turnContext = null
    )
    {
        _inner = inner;
        _timeProvider = timeProvider;
        _turnContext = turnContext;
    }

    public void StageRequest(AgentSession session, IReadOnlyList<ChatMessage> messages) =>
        _inner.GetService<IConversationHistoryRequests>()?.StageRequest(session, messages);

    public async ValueTask PersistPendingAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken)
    {
        try
        {
            if (_captures.TryGetValue(session, out var capture))
                await capture
                    .FinishAsync(
                        ConversationHistoryPersistenceContext.HasExecutionFailure
                            ? ConversationMessageState.Failed
                            : ConversationMessageState.Interrupted,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
        }
        finally
        {
            End(session);
        }
        if (_inner.GetService<IConversationHistoryRequests>() is { } requests)
            await requests.PersistPendingAsync(agent, session, cancellationToken).ConfigureAwait(false);
    }

    public override IReadOnlyList<string> StateKeys => _inner.StateKeys;

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _inner.GetService(serviceType, serviceKey);

    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => _inner.InvokingAsync(context, cancellationToken);

    internal async ValueTask<Capture?> BeginAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<ChatMessage> input,
        IAgentMessageAdapter<AgentResponseUpdate> adapter,
        bool external,
        CancellationToken token
    )
    {
        var writer = _inner.GetService<IConversationMessageWriter>();
        var scope = _inner.GetService<IProviderSessionState>()?.GetMessageWriteScope(session);
        if (writer == null || scope == null)
            return null;
        if (_turnContext?.Current is { } turn && turn.ProjectId == scope.ProjectId && turn.ContextId == scope.ContextId)
            scope = scope with { TurnId = turn.Task.TaskId };
        // Projects' existing callback stages/deduplicates original requests and takes the prelude.
        if (_inner.GetService<IConversationMessageInputs>() is { } inputs)
            await inputs.PersistInputsAsync(session, input, scope.ProducerId, token).ConfigureAwait(false);
        else
            await _inner.InvokedAsync(new InvokedContext(agent, session, input, []), token).ConfigureAwait(false);
        var capture = new Capture(scope, writer, adapter, _timeProvider, external, agent.Name);
        if (!_captures.TryAdd(session, capture))
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        return capture;
    }

    public async ValueTask<IStreamingConversationHistory?> BeginStreamingResponseAsync(
        AIAgent agent,
        AgentSession session,
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        return await BeginAsync(
                    agent,
                    session,
                    requestMessages
                        .Where(message =>
                            message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External
                        )
                        .ToList(),
                    new ModelMessageAdapter(),
                    false,
                    cancellationToken
                )
                .ConfigureAwait(false)
            ?? await (
                _inner
                    .GetService<IStreamingConversationHistoryProvider>()
                    ?.BeginStreamingResponseAsync(agent, session, requestMessages, cancellationToken)
                ?? ValueTask.FromResult<IStreamingConversationHistory?>(null)
            );
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (context.Session != null && _captures.TryGetValue(context.Session, out var capture))
        {
            if (context.InvokeException == null)
                await capture
                    .CompleteAsync((context.ResponseMessages ?? []).ToList(), cancellationToken)
                    .ConfigureAwait(false);
            if (!capture.External)
            {
                try
                {
                    await capture
                        .FinishAsync(
                            context.InvokeException == null ? ConversationMessageState.Completed
                                : context.InvokeException is OperationCanceledException
                                    ? ConversationMessageState.Interrupted
                                : ConversationMessageState.Failed,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                finally
                {
                    End(context.Session);
                }
            }
            return;
        }
        if (context.Session != null && context.InvokeException == null && context.ResponseMessages?.Any() == true)
        {
            var created = await BeginAsync(
                    context.Agent,
                    context.Session,
                    context.RequestMessages.ToList(),
                    new ModelMessageAdapter(),
                    false,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (created != null)
            {
                try
                {
                    await created
                        .CompleteAsync(context.ResponseMessages.ToList(), cancellationToken)
                        .ConfigureAwait(false);
                    await created
                        .FinishAsync(ConversationMessageState.Completed, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    End(context.Session);
                }
                return;
            }
        }
        await _inner.InvokedAsync(context, cancellationToken).ConfigureAwait(false);
    }

    internal void End(AgentSession session)
    {
        _captures.TryRemove(session, out _);
    }

    internal sealed class Capture : IStreamingConversationHistory
    {
        private readonly ConversationMessageWriteScope _scope;
        private readonly IConversationMessageWriter _writer;
        private readonly IAgentMessageAdapter<AgentResponseUpdate> _adapter;
        private readonly AgentMessageProjection _projection;
        private readonly ConcurrentQueue<ChatMessage> _outgoing = new();
        private bool _failed;
        private readonly string? _agentName;
        public bool External { get; }

        internal Capture(
            ConversationMessageWriteScope scope,
            IConversationMessageWriter writer,
            IAgentMessageAdapter<AgentResponseUpdate> adapter,
            TimeProvider timeProvider,
            bool external,
            string? agentName
        )
        {
            _scope = scope;
            _writer = writer;
            _adapter = adapter;
            _projection = new AgentMessageProjection(scope, timeProvider);
            External = external;
            _agentName = agentName;
        }

        internal async ValueTask<bool> ProcessAsync(AgentResponseUpdate update, CancellationToken token)
        {
            _failed |= update
                .Contents.OfType<ErrorContent>()
                .Any(error =>
                    error.AdditionalProperties?.GetValueOrDefault("isFatalError")?.ToString() == bool.TrueString
                );
            var changedBytes = 0L;
            var operations = _adapter.Map(update);
            foreach (var operation in operations)
            {
                PrepareHeader(operation.Header);
                if (_projection.Apply(operation) is { } changed)
                {
                    _outgoing.Enqueue(changed);
                    changedBytes += EstimateBytes(changed);
                }
            }
            if (changedBytes > 0)
                await _writer.ScheduleAsync(_scope, _projection, changedBytes, token).ConfigureAwait(false);
            return operations.Count > 0;
        }

        public async ValueTask AppendAsync(ChatResponseUpdate update, CancellationToken cancellationToken) =>
            await AppendUpdateAsync(update, cancellationToken).ConfigureAwait(false);

        /// <summary>Returns false when no operation claimed the update, so the caller keeps delivering it.</summary>
        internal async ValueTask<bool> AppendUpdateAsync(
            ChatResponseUpdate update,
            CancellationToken cancellationToken
        ) =>
            await ProcessAsync(
                new AgentResponseUpdate
                {
                    MessageId = update.MessageId,
                    Role = update.Role,
                    AuthorName = update.AuthorName,
                    Contents = update.Contents,
                    CreatedAt = update.CreatedAt,
                    AdditionalProperties = update.AdditionalProperties,
                    FinishReason = update.FinishReason,
                },
                cancellationToken
            );

        public async ValueTask CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
        {
            var bytes = 0L;
            foreach (var message in messages)
            {
                foreach (var operation in _adapter.MapSnapshot(message))
                {
                    PrepareHeader(operation.Header);
                    if (_projection.Apply(operation) is { } changed)
                    {
                        if (External)
                            _outgoing.Enqueue(changed);
                        bytes += EstimateBytes(changed);
                    }
                }
            }
            if (bytes > 0)
                await _writer.ScheduleAsync(_scope, _projection, bytes, cancellationToken).ConfigureAwait(false);
        }

        internal async ValueTask FinishAsync(ConversationMessageState state, CancellationToken token)
        {
            if (_failed && state == ConversationMessageState.Completed)
                state = ConversationMessageState.Failed;
            foreach (var operation in _adapter.MapFinalization())
            {
                PrepareHeader(operation.Header);
                if (_projection.Apply(operation) is { } completed && External)
                    _outgoing.Enqueue(completed);
            }
            foreach (var message in _projection.SealOpen(state))
                if (External)
                    _outgoing.Enqueue(message);
            await _writer.ScheduleAsync(_scope, _projection, 1, token).ConfigureAwait(false);
        }

        internal IEnumerable<AgentResponseUpdate> Drain()
        {
            while (_outgoing.TryDequeue(out var message))
                yield return new AgentResponseUpdate
                {
                    MessageId = message.MessageId,
                    Role = message.Role,
                    AuthorName = message.AuthorName,
                    Contents = message.Contents,
                    CreatedAt = message.CreatedAt,
                    AdditionalProperties = message.AdditionalProperties,
                };
        }

        private void PrepareHeader(ChatMessage message)
        {
            message.AdditionalProperties =
                message.AdditionalProperties == null ? [] : new(message.AdditionalProperties);
            if (External)
                ExternalAgentChatHistoryAgent.MarkDisplayOnlyMessage(message);
            if (string.IsNullOrWhiteSpace(message.AuthorName) && !string.IsNullOrWhiteSpace(_agentName))
                message.AdditionalProperties.TryAdd("agentName", _agentName);
        }

        private static long EstimateBytes(ChatMessage message) =>
            256
            + message.Contents.Sum(content =>
                content switch
                {
                    TextContent text => (long)Encoding.UTF8.GetByteCount(text.Text ?? ""),
                    TextReasoningContent reasoning => Encoding.UTF8.GetByteCount(reasoning.Text ?? ""),
                    _ => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(content).LongLength,
                }
            );
    }
}
