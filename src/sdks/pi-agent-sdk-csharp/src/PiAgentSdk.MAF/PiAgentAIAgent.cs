using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PiAgentSdk.MAF.Internal;

namespace PiAgentSdk.MAF;

/// <summary>Adapts a stateful Pi RPC session to the Microsoft Agent Framework <see cref="AIAgent"/> contract.</summary>
/// <remarks>
/// Pi owns provider-side conversation history. Dispose this Agent to terminate every live Pi RPC process created by
/// its MAF sessions.
/// </remarks>
public sealed class PiAgentAIAgent : AIAgent, IAsyncDisposable
{
    private readonly PiAgentAIAgentOptions _options;
    private readonly PiAgent _piAgent;
    private readonly ILogger? _logger;
    private readonly ConcurrentQueue<PiSession> _liveSessions = new();
    private int _disposed;

    /// <summary>Initializes the adapter with default Pi options and no logger.</summary>
    public PiAgentAIAgent()
        : this(new PiAgentAIAgentOptions(), logger: null) { }

    /// <summary>Initializes the adapter with the supplied options and optional logger.</summary>
    /// <param name="options">MAF and Pi session options, or <see langword="null"/> for defaults.</param>
    /// <param name="logger">An optional logger for sanitized diagnostics.</param>
    public PiAgentAIAgent(PiAgentAIAgentOptions? options, ILogger? logger = null)
        : this(options ?? new PiAgentAIAgentOptions(), logger, piAgent: null) { }

    internal PiAgentAIAgent(PiAgentAIAgentOptions options, ILogger? logger, PiAgent? piAgent)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (
            options.SessionOptions.NoSession
            && (options.IsResume || options.SessionId != null || options.OnSessionStartedAsync != null)
        )
        {
            throw new ArgumentException(
                "Ephemeral Pi sessions cannot use MAF resume or session-start callbacks.",
                nameof(options)
            );
        }
        if (options.HistoryPersistenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "History persistence timeout must be positive.");
        }

        _options = options;
        _logger = logger;
        _piAgent = piAgent ?? new PiAgent(options.GlobalOptions, logger);
        ChatHistoryProvider = options.ChatHistoryProvider;
    }

    /// <inheritdoc />
    public override string Name => "Pi";

    /// <summary>Gets the configured history provider, when history persistence is enabled.</summary>
    public ChatHistoryProvider? ChatHistoryProvider { get; }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ValueTask.FromResult<AgentSession>(new PiAgentSession(_options.IsResume ? _options.SessionId : null));
    }

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not PiAgentSession piSession)
        {
            throw new InvalidOperationException($"Expected {nameof(PiAgentSession)} but got {session.GetType().Name}.");
        }

        var options = jsonSerializerOptions ?? PiAgentSessionJson.Options;
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(piSession, options));
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        var options = jsonSerializerOptions ?? PiAgentSessionJson.Options;
        var session =
            serializedState.Deserialize<PiAgentSession>(options)
            ?? throw new ArgumentException("Unable to deserialize Pi session state.", nameof(serializedState));
        return ValueTask.FromResult<AgentSession>(session);
    }

    /// <inheritdoc />
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var requestMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var safeSession = await PrepareSessionAsync(session, requestMessages, cancellationToken).ConfigureAwait(false);
        var prompt = PiMafPromptBuilder.Create(requestMessages);
        if (prompt == null)
        {
            return new AgentResponse { ResponseId = Guid.CreateVersion7().ToString("N"), Messages = [] };
        }

        await PersistWithTimeoutAsync(safeSession, requestMessages, []).ConfigureAwait(false);
        var piSession = await GetOrCreatePiSessionAsync(safeSession, cancellationToken).ConfigureAwait(false);
        var responseMessages = new List<ChatMessage>();
        var usage = new PiUsage();
        var hasUsage = false;
        var eventMapper = new PiEventMapper(_options.SessionOptions.Model);

        await foreach (
            var evt in piSession.RunStreamingAsync(prompt.Text, prompt.Images, cancellationToken).ConfigureAwait(false)
        )
        {
            if (evt is PiTurnEndEvent turnEnd)
            {
                var history = PiEventMapper.ToHistoryMessages(turnEnd, _options.SessionOptions.Model);
                responseMessages.AddRange(history);
                await PersistWithTimeoutAsync(safeSession, [], history).ConfigureAwait(false);
                AddTurnUsage(turnEnd, ref usage, ref hasUsage);
            }
            else if (evt is PiCompactionEvent { Type: "compaction_end", Result.Usage: not null } compaction)
            {
                usage += compaction.Result.Usage;
                hasUsage = true;
            }

            var update = eventMapper.ToUpdate(evt);
            if (
                evt is not PiMessageEvent
                && evt is not PiTurnEndEvent
                && evt is not PiToolExecutionEvent { Type: "tool_execution_end" }
                && update?.Contents.Any(content => content is ErrorContent) == true
            )
            {
                var errorContents = update.Contents.Where(content => content is ErrorContent).ToList();
                responseMessages.Add(
                    new ChatMessage(update.Role ?? ChatRole.System, errorContents)
                    {
                        AuthorName = update.AuthorName,
                        MessageId = update.MessageId,
                        AdditionalProperties = update.AdditionalProperties,
                    }
                );
            }
        }

        return new AgentResponse
        {
            ResponseId = Guid.CreateVersion7().ToString("N"),
            Messages = responseMessages,
            Usage = hasUsage ? PiEventMapper.ToUsageDetails(usage) : null,
        };
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var requestMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var safeSession = await PrepareSessionAsync(session, requestMessages, cancellationToken).ConfigureAwait(false);
        var prompt = PiMafPromptBuilder.Create(requestMessages);
        if (prompt == null)
        {
            yield break;
        }

        await PersistWithTimeoutAsync(safeSession, requestMessages, []).ConfigureAwait(false);
        var piSession = await GetOrCreatePiSessionAsync(safeSession, cancellationToken).ConfigureAwait(false);
        var eventMapper = new PiEventMapper(_options.SessionOptions.Model);
        await foreach (
            var evt in piSession.RunStreamingAsync(prompt.Text, prompt.Images, cancellationToken).ConfigureAwait(false)
        )
        {
            if (evt is PiTurnEndEvent turnEnd)
            {
                var history = PiEventMapper.ToHistoryMessages(turnEnd, _options.SessionOptions.Model);
                await PersistWithTimeoutAsync(safeSession, [], history).ConfigureAwait(false);
            }

            var update = eventMapper.ToUpdate(evt);
            if (update != null)
            {
                yield return update;
            }
        }
    }

    /// <summary>Disposes every live Pi session and terminates its owned RPC process.</summary>
    /// <returns>A task representing asynchronous cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_liveSessions.TryDequeue(out var session))
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Failed to dispose a Pi session.");
            }
        }

        GC.SuppressFinalize(this);
    }

    private async ValueTask<PiAgentSession> PrepareSessionAsync(
        AgentSession? session,
        IReadOnlyList<ChatMessage> requestMessages,
        CancellationToken cancellationToken
    )
    {
        session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session is not PiAgentSession piSession)
        {
            throw new InvalidOperationException($"Expected {nameof(PiAgentSession)} but got {session.GetType().Name}.");
        }

        if (ChatHistoryProvider != null)
        {
#pragma warning disable MAAI001
            var invoking = new ChatHistoryProvider.InvokingContext(this, piSession, requestMessages);
#pragma warning restore MAAI001
            _ = await ChatHistoryProvider.InvokingAsync(invoking, cancellationToken).ConfigureAwait(false);
        }

        return piSession;
    }

    private async ValueTask<PiSession> GetOrCreatePiSessionAsync(
        PiAgentSession session,
        CancellationToken cancellationToken
    )
    {
        if (session.BoundSession != null)
        {
            return session.BoundSession;
        }

        await session.BindLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.BoundSession != null)
            {
                return session.BoundSession;
            }

            var existingId = session.SessionId;
            var piSession = string.IsNullOrWhiteSpace(existingId)
                ? _piAgent.StartSession(_options.SessionOptions)
                : _piAgent.ResumeSession(existingId, _options.SessionOptions);
            try
            {
                await piSession.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await piSession.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            session.BoundSession = piSession;
            _liveSessions.Enqueue(piSession);
            if (string.IsNullOrWhiteSpace(existingId))
            {
                session.SessionId = piSession.Id;
                if (_options.OnSessionStartedAsync != null && piSession.Id != null)
                {
                    await NotifySessionStartedWithTimeoutAsync(piSession.Id).ConfigureAwait(false);
                }
            }

            return piSession;
        }
        finally
        {
            session.BindLock.Release();
        }
    }

    private ValueTask PersistAsync(
        PiAgentSession session,
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages,
        CancellationToken cancellationToken
    )
    {
        if (ChatHistoryProvider == null)
        {
            return ValueTask.CompletedTask;
        }

#pragma warning disable MAAI001
        var invoked = new ChatHistoryProvider.InvokedContext(this, session, requestMessages, responseMessages);
#pragma warning restore MAAI001
        return ChatHistoryProvider.InvokedAsync(invoked, cancellationToken);
    }

    private async ValueTask PersistWithTimeoutAsync(
        PiAgentSession session,
        IEnumerable<ChatMessage> requestMessages,
        IEnumerable<ChatMessage> responseMessages
    )
    {
        using var timeout = new CancellationTokenSource(_options.HistoryPersistenceTimeout);
        var persistenceTask = PersistAsync(session, requestMessages, responseMessages, timeout.Token).AsTask();
        try
        {
            await persistenceTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            if (!persistenceTask.IsCompleted)
            {
                _ = ObserveTimedOutOperationAsync(persistenceTask, "history persistence");
            }

            throw new TimeoutException(
                $"Pi history persistence did not complete within {_options.HistoryPersistenceTimeout}.",
                exception
            );
        }
    }

    private async ValueTask NotifySessionStartedWithTimeoutAsync(string sessionId)
    {
        using var timeout = new CancellationTokenSource(_options.HistoryPersistenceTimeout);
        var callbackTask = _options.OnSessionStartedAsync!(sessionId, timeout.Token).AsTask();
        try
        {
            await callbackTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            if (!callbackTask.IsCompleted)
            {
                _ = ObserveTimedOutOperationAsync(callbackTask, "session-start persistence");
            }

            throw new TimeoutException(
                $"Pi session-start persistence did not complete within {_options.HistoryPersistenceTimeout}.",
                exception
            );
        }
    }

    private async Task ObserveTimedOutOperationAsync(Task operation, string operationName)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Timed-out Pi {OperationName} later failed.", operationName);
        }
    }

    private static void AddTurnUsage(PiTurnEndEvent turnEnd, ref PiUsage usage, ref bool hasUsage)
    {
        if (turnEnd.Message is PiAssistantMessage { Usage: not null } assistant)
        {
            usage += assistant.Usage;
            hasUsage = true;
        }

        foreach (var result in turnEnd.ToolResults.OfType<PiToolResultMessage>())
        {
            if (result.Usage != null)
            {
                usage += result.Usage;
                hasUsage = true;
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
