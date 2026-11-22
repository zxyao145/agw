using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using global::A2A;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using static Agw.Agents.ExternalAgents.A2A.A2AClientDiagnostics;
using static Agw.Agents.ExternalAgents.A2A.A2ARequestErrors;

namespace Agw.Agents.Execution.Agents.ExternalAgents.A2A;

public sealed class A2ARemoteAgent : AIAgent, IAsyncDisposable
{
    private static readonly TimeSpan CancelTaskTimeout = TimeSpan.FromSeconds(5);
    private const int ResubscribeBackoffMultiplier = 2;
    private readonly IA2AClient _client;
    private readonly A2ARemoteHttpClient _http;
    private readonly bool _streaming;
    private readonly Guid _interfaceRevision;
    private readonly A2ARemoteAgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly string _interfaceHost;
    private readonly string _protocolBinding;

    private A2ARemoteAgent(
        Agent agent,
        (IA2AClient Protocol, A2ARemoteHttpClient Http) clients,
        A2ARemoteAgentOptions options,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        var endpoint = agent.A2AEndpoint!;
        (_client, _http) = clients;
        IdCore = agent.Id.ToString("D");
        Name = agent.Name;
        Description = agent.Description;
        _streaming = endpoint.Streaming;
        _interfaceRevision = endpoint.InterfaceRevision;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _interfaceHost = _http.Target.IdnHost;
        _protocolBinding = endpoint.ProtocolBinding;
    }

    internal static A2ARemoteAgent Create(
        Agent agent,
        IA2ARemoteHttpClientFactory httpClients,
        A2ARemoteAgentOptions options,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        var endpoint =
            agent.A2AEndpoint ?? throw new AgwException(ErrorCodes.InvalidParam, "The A2A agent has no endpoint.");
        var target = new Uri(endpoint.InterfaceUrl);
        var path = new Uri(target.GetLeftPart(UriPartial.Path));
        var http = httpClients.Create(target, A2AEndpointCredentialsJson.Deserialize(endpoint.Credentials));
        IA2AClient? client = null;
        try
        {
            client = endpoint.ProtocolBinding switch
            {
                ProtocolBindingNames.HttpJson => new A2AHttpJsonClient(path, http.Client),
                ProtocolBindingNames.JsonRpc => new A2AClient(path, http.Client),
                _ => throw new AgwException(ErrorCodes.A2AAgentCardInvalid),
            };
            return new A2ARemoteAgent(agent, (client, http), options, timeProvider, logger);
        }
        catch
        {
            (client as IDisposable)?.Dispose();
            http.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            (_client as IDisposable)?.Dispose();
        }
        finally
        {
            _http.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    protected override string IdCore { get; }
    public override string Name { get; }
    public override string Description { get; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new A2ARemoteAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(RequireSession(session).Serialize(jsonSerializerOptions));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<AgentSession>(A2ARemoteAgentSession.Deserialize(serializedSession, jsonSerializerOptions));

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    ) => RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var remote = session is null ? new A2ARemoteAgentSession() : RequireSession(session);
        remote.BindEndpointRevision(_interfaceRevision);
        var turn = new A2ARemoteTurn(remote);
        var parent = Activity.Current;
        using var activity = StartTurn(Id, _interfaceHost, _protocolBinding, _streaming);
        var turnActivity = activity ?? parent;
        var started = _timeProvider.GetTimestamp();
        string? continuedTaskId = null;
        LogTurnStarted(remote);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = CreateRequest(messages, remote);
            continuedTaskId = request.Message.TaskId;
            var updates = _streaming
                ? RunStreamingTurnAsync(request, turn, cancellationToken)
                : RunPollingTurnAsync(request, turn, cancellationToken);
            await using var enumerator = updates.GetAsyncEnumerator();
            while (await ReadTurnUpdateAsync(enumerator, turn, turnActivity))
            {
                yield return enumerator.Current;
            }

            cancellationToken.ThrowIfCancellationRequested();
            turn.Outcome = A2ARemoteTurn.IsAwaitingInput(remote.TaskState)
                ? TurnOutcome.InputRequired
                : TurnOutcome.Completed;
            if (turn.CreateResult() is { } result)
            {
                yield return result;
            }
        }
        finally
        {
            Activity.Current = turnActivity;
            await FinishTurnAsync(turn, continuedTaskId, activity, started, cancellationToken);
        }
    }

    private void LogTurnStarted(A2ARemoteAgentSession session) =>
        _logger.LogInformation(
            "A2A turn started for Agent {AgentId} at {InterfaceHost}, binding {ProtocolBinding}, "
                + "streaming {Streaming}, context {ContextId}, task {TaskId}, state {TaskState}.",
            Id,
            _interfaceHost,
            _protocolBinding,
            _streaming,
            session.ContextId,
            session.TaskId,
            A2ARemoteAgentSession.FormatTaskState(session.TaskState)
        );

    private SendMessageRequest CreateRequest(IEnumerable<ChatMessage> messages, A2ARemoteAgentSession session)
    {
        var parts = messages
            .SelectMany(message => message.Contents)
            .Select(content => content.ToPart())
            .OfType<Part>()
            .ToList();
        if (parts.Count == 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "The A2A input must contain at least one supported content part."
            );
        }

        return new SendMessageRequest
        {
            Message = new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Role = Role.User,
                Parts = parts,
                ContextId = session.ContextId,
                TaskId = A2ARemoteTurn.IsAwaitingInput(session.TaskState) ? session.TaskId : null,
            },
            Configuration = _streaming ? null : new SendMessageConfiguration { ReturnImmediately = true },
        };
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingTurnAsync(
        SendMessageRequest request,
        A2ARemoteTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        string? subscriptionTaskId = null;
        var idleTimedOut = false;
        while (true)
        {
            using var deadline = new StreamReadDeadline(_http, _options, cancellationToken);
            await foreach (var update in ReadStreamAsync(request, turn, deadline, subscriptionTaskId))
            {
                yield return update;
            }
            if (turn.IsComplete)
                yield break;

            cancellationToken.ThrowIfCancellationRequested();
            idleTimedOut |= deadline.IdleTimedOut;
            var task = await RecoverClosedStreamAsync(
                request,
                turn.Session,
                subscriptionTaskId,
                deadline,
                cancellationToken
            );
            if (task is not null)
            {
                foreach (var update in turn.ProcessTask(task, recovered: true))
                {
                    yield return update;
                }
            }
            if (turn.IsComplete)
                yield break;

            await ScheduleResubscribeAsync(turn, deadline.Failure, idleTimedOut, cancellationToken);
            subscriptionTaskId = turn.ObservedTaskId;
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> ReadStreamAsync(
        SendMessageRequest request,
        A2ARemoteTurn turn,
        StreamReadDeadline deadline,
        string? subscriptionTaskId
    )
    {
        var stream = subscriptionTaskId is null
            ? _client.SendStreamingMessageAsync(request, deadline.Token)
            : _client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = subscriptionTaskId }, deadline.Token);
        await using var reader = stream.GetAsyncEnumerator();
        while (!turn.IsComplete && await deadline.ReadNextAsync(reader))
        {
            foreach (var update in turn.Process(reader.Current, recovered: subscriptionTaskId is not null))
            {
                yield return update;
            }
        }
    }

    private async Task<AgentTask?> RecoverClosedStreamAsync(
        SendMessageRequest request,
        A2ARemoteAgentSession session,
        string? subscriptionTaskId,
        StreamReadDeadline deadline,
        CancellationToken cancellationToken
    )
    {
        // SDK 的 HTTP+JSON 流错误可能呈现为空事件。通过 GetTask 确认 task 的状态。
        // SDK HTTP+JSON stream errors can surface as empty events. Confirm task state with GetTask.
        var failure = deadline.Failure;
        var readSnapshot =
            subscriptionTaskId is not null
            && (deadline.EmptyEvent || failure is A2AException { ErrorCode: A2AErrorCode.UnsupportedOperation });
        if (failure is not null)
        {
            var cannotRecover =
                !IsRecoverableStreamFailure(failure) || deadline.RequestTimedOut && subscriptionTaskId is null;
            if (!readSnapshot && cannotRecover)
            {
                throw MapRequestFailure(
                    failure,
                    session,
                    subscriptionTaskId ?? request.Message.TaskId,
                    deadline.RequestTimedOut
                );
            }
            if (readSnapshot)
                LogStreamReadFailure(failure);
        }
        return readSnapshot ? await GetTaskAsync(session, subscriptionTaskId!, cancellationToken) : null;
    }

    private async Task ScheduleResubscribeAsync(
        A2ARemoteTurn turn,
        Exception? failure,
        bool idleTimedOut,
        CancellationToken cancellationToken
    )
    {
        if (!turn.HasActiveTask)
        {
            const string message = "The remote A2A stream ended before the turn completed.";
            var code = ErrorCodes.A2ARemoteAgentRequestFailed;
            throw failure is null
                ? new AgwException(code, message)
                : new AgwException(code, message, SafeException(failure));
        }
        if (turn.ResubscribeCount >= _options.ResubscribeAttempts)
        {
            var code = idleTimedOut ? ErrorCodes.A2ARemoteAgentTimeout : ErrorCodes.A2ARemoteAgentRequestFailed;
            throw failure is null
                ? new AgwException(code)
                : new AgwException(code, code.Message, SafeException(failure));
        }

        if (failure is not null)
            LogStreamReadFailure(failure);

        await Task.Delay(
            TimeSpan.FromSeconds(Math.Pow(ResubscribeBackoffMultiplier, turn.ResubscribeCount)),
            _timeProvider,
            cancellationToken
        );
        turn.ResubscribeCount++;
        RecordResubscribe();
    }

    private void LogStreamReadFailure(Exception failure) =>
        _logger.LogError(
            SafeException(failure),
            "A2A stream read for Agent {AgentId} stopped before the task completed.",
            Id
        );

    private async IAsyncEnumerable<AgentResponseUpdate> RunPollingTurnAsync(
        SendMessageRequest request,
        A2ARemoteTurn turn,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var response = await SendAsync(
            token => _client.SendMessageAsync(request, token),
            turn.Session,
            request.Message.TaskId,
            cancellationToken
        );
        if (response.Message is { } message)
        {
            foreach (var update in turn.ProcessMessage(message))
            {
                yield return update;
            }
            yield break;
        }

        var task =
            response.Task
            ?? throw new AgwException(
                ErrorCodes.A2ARemoteAgentRequestFailed,
                "The remote A2A agent returned an empty response."
            );
        while (true)
        {
            foreach (var update in turn.ProcessTask(task, includeArtifacts: !A2ARemoteTurn.IsActive(task.Status.State)))
            {
                yield return update;
            }
            if (turn.IsComplete)
            {
                yield break;
            }

            await Task.Delay(_options.PollInterval, _timeProvider, cancellationToken);
            task = await GetTaskAsync(turn.Session, task.Id, cancellationToken);
        }
    }

    private async ValueTask<bool> ReadTurnUpdateAsync(
        IAsyncEnumerator<AgentResponseUpdate> updates,
        A2ARemoteTurn turn,
        Activity? turnActivity
    )
    {
        // 每次读取都恢复 Turn span，使 SDK span 成为其子级。
        // Restore the turn span on every read so SDK spans become its children.
        Activity.Current = turnActivity;
        try
        {
            return await updates.MoveNextAsync();
        }
        catch (AgwException exception)
        {
            if (exception.Code == ErrorCodes.A2ARemoteAgentUnauthorized.Code)
                turn.Outcome = TurnOutcome.Unauthorized;
            else if (exception.Code == ErrorCodes.A2ARemoteAgentTimeout.Code)
                turn.Outcome = TurnOutcome.Timeout;
            _logger.LogWarning(
                SafeException(exception),
                "A2A turn for Agent {AgentId} failed with {ErrorCode}.",
                Id,
                exception.Code
            );
            throw;
        }
    }

    private async Task FinishTurnAsync(
        A2ARemoteTurn turn,
        string? continuedTaskId,
        Activity? activity,
        long started,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (cancellationToken.IsCancellationRequested && !turn.IsComplete)
            {
                turn.Outcome = TurnOutcome.Interrupted;
                await CancelRemoteTaskAsync(turn, continuedTaskId);
            }
        }
        finally
        {
            RecordTurnFinished(turn, activity, _timeProvider.GetElapsedTime(started));
        }
    }

    private void RecordTurnFinished(A2ARemoteTurn turn, Activity? activity, TimeSpan elapsed)
    {
        var session = turn.Session;
        var state = A2ARemoteAgentSession.FormatTaskState(session.TaskState);
        activity
            ?.SetTag("a2a.context_id", session.ContextId)
            .SetTag("a2a.task_id", session.TaskId)
            .SetTag("a2a.task_state", state)
            .SetTag("a2a.resubscribe_count", turn.ResubscribeCount);
        RecordTurn(activity, turn.Outcome, _protocolBinding, elapsed);
        _logger.LogInformation(
            "A2A turn for Agent {AgentId} at {InterfaceHost}, binding {ProtocolBinding}, "
                + "streaming {Streaming}, context {ContextId}, task {TaskId}, state {TaskState}, "
                + "with {ResubscribeCount} resubscribes finished with {Outcome} in {ElapsedMs} ms.",
            Id,
            _interfaceHost,
            _protocolBinding,
            _streaming,
            session.ContextId,
            session.TaskId,
            state,
            turn.ResubscribeCount,
            OutcomeName(turn.Outcome),
            elapsed.TotalMilliseconds
        );
    }

    private Task<AgentTask> GetTaskAsync(
        A2ARemoteAgentSession session,
        string taskId,
        CancellationToken cancellationToken
    ) =>
        SendAsync(
            token => _client.GetTaskAsync(new GetTaskRequest { Id = taskId }, token),
            session,
            taskId,
            cancellationToken
        );

    private async Task<T> SendAsync<T>(
        Func<CancellationToken, Task<T>> send,
        A2ARemoteAgentSession session,
        string? taskId,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);
        try
        {
            return await _http.InvokeAsync(() => send(deadline.Token));
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw MapRequestFailure(exception, session, taskId, deadline.IsCancellationRequested);
        }
    }

    private async Task CancelRemoteTaskAsync(A2ARemoteTurn turn, string? continuedTaskId)
    {
        // 收到首个续答事件之前，使用续答请求中的 taskId 完成取消。
        // Before the first continuation event, cancel using the task ID sent in the continuation request.
        var taskId =
            turn.HasActiveTask ? turn.ObservedTaskId
            : turn.ObservedTaskId is null ? continuedTaskId
            : null;
        if (taskId is null)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(CancelTaskTimeout, _timeProvider);
        try
        {
            await _http.InvokeAsync(() =>
                _client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, timeout.Token)
            );
            turn.Session.RecordTask(taskId, TaskState.Canceled);
            _logger.LogInformation("Canceled A2A task {TaskId} for Agent {AgentId}.", taskId, Id);
        }
        catch (Exception exception) when (IsRequestException(exception))
        {
            if (A2ARemoteTurn.IsAwaitingInput(turn.Session.TaskState))
            {
                turn.Session.ClearTask();
            }
            _logger.LogWarning(
                SafeException(exception),
                "Could not cancel A2A task {TaskId} for Agent {AgentId}.",
                taskId,
                Id
            );
        }
    }

    private static AgwException MapRequestFailure(
        Exception exception,
        A2ARemoteAgentSession session,
        string? requestedTaskId,
        bool timedOut
    )
    {
        var cause = SafeException(exception);
        if (timedOut)
        {
            var code = ErrorCodes.A2ARemoteAgentTimeout;
            return new AgwException(code, code.Message, cause);
        }

        if (PreserveKnownError(exception, cause) is { } knownError)
        {
            return knownError;
        }

        var failure = ErrorCodes.A2ARemoteAgentRequestFailed;
        if (exception is A2AException protocol)
        {
            if (requestedTaskId is not null && protocol.ErrorCode == A2AErrorCode.TaskNotFound)
            {
                session.ClearRemoteState();
            }
            return new AgwException(failure, $"The remote A2A agent returned {protocol.ErrorCode}.", cause);
        }
        if (FindHttpError(exception)?.StatusCode is { } status)
        {
            return new AgwException(failure, $"The remote A2A agent returned HTTP {(int)status}.", cause);
        }
        return new AgwException(failure, failure.Message, cause);
    }

    private static bool IsRecoverableStreamFailure(Exception exception) =>
        exception is IOException or JsonException or OperationCanceledException
        || exception is HttpRequestException { StatusCode: null } && exception.GetBaseException() is not AgwException;

    private static A2ARemoteAgentSession RequireSession(AgentSession session) =>
        session as A2ARemoteAgentSession
        ?? throw new AgwException(ErrorCodes.InvalidParam, "A2A requires an A2ARemoteAgentSession.");

    private sealed class StreamReadDeadline : IDisposable
    {
        private readonly A2ARemoteHttpClient _http;
        private readonly A2ARemoteAgentOptions _options;
        private readonly CancellationTokenSource _source;
        private bool _headersReceived;
        public CancellationToken Token => _source.Token;
        public bool RequestTimedOut => Token.IsCancellationRequested && !_headersReceived;
        public bool IdleTimedOut => Token.IsCancellationRequested && _headersReceived;
        public bool EmptyEvent { get; private set; }
        public Exception? Failure { get; private set; }

        public StreamReadDeadline(
            A2ARemoteHttpClient http,
            A2ARemoteAgentOptions options,
            CancellationToken cancellationToken
        )
        {
            _http = http;
            _options = options;
            _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public async ValueTask<bool> ReadNextAsync(IAsyncEnumerator<StreamResponse> reader)
        {
            _source.CancelAfter(_headersReceived ? _options.StreamIdleTimeout : _options.RequestTimeout);
            try
            {
                var received = await _http.InvokeAsync(reader.MoveNextAsync, OnHeaders);
                _headersReceived |= received;
                EmptyEvent = received && reader.Current.PayloadCase == StreamResponseCase.None;
                return received && !EmptyEvent;
            }
            catch (Exception exception) when (IsRequestException(exception))
            {
                Failure = exception;
                return false;
            }
            finally
            {
                _source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        private ValueTask OnHeaders(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            _headersReceived = true;
            _source.CancelAfter(_options.StreamIdleTimeout);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _source.Dispose();
    }
}
