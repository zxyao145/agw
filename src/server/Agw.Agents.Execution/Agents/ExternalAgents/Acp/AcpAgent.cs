using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TurnOutcome = Agw.Agents.ExternalAgents.Acp.AcpClientDiagnostics.TurnOutcome;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed record AcpAgentRuntimeOptions(
    Guid AgentId,
    string AgentName,
    AcpAgentSettings Settings,
    string WorkingDirectory,
    IReadOnlyList<string> AdditionalDirectories,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? ProviderSessionId,
    Func<string, CancellationToken, ValueTask>? OnSessionStartedAsync,
    AcpAgentOptions Options
);

internal sealed class AcpAgent : AIAgent, IAsyncDisposable, IAcpClientHandler
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly AcpAgentRuntimeOptions _runtime;
    private readonly AcpLaunchResolver _launcher;
    private readonly AcpPermissionBridge _permissions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _openSessions = new(StringComparer.Ordinal);
    private AcpConnection? _connection;
    private PromptTurn? _runningTurn;
    private int _disposed;

    public AcpAgent(
        AcpAgentRuntimeOptions runtime,
        AcpLaunchResolver launcher,
        AcpPermissionBridge permissions,
        ILogger logger,
        TimeProvider timeProvider
    )
    {
        _runtime = runtime;
        _launcher = launcher;
        _permissions = permissions;
        _timeProvider = timeProvider;
        _logger = logger;
        IdCore = runtime.AgentId.ToString("D");
        Name = runtime.AgentName;
    }

    protected override string IdCore { get; }
    public override string Name { get; }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new AcpAgentSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult(RequireSession(session).Serialize(jsonSerializerOptions));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<AgentSession>(AcpAgentSession.Deserialize(serializedSession, jsonSerializerOptions));

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
        var current = session is null ? new AcpAgentSession() : RequireSession(session);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_runGate.Wait(0, cancellationToken))
            throw new AgwException(ErrorCodes.AgentExecutionFailed, "Concurrent ACP turns cannot share a runtime.");

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await foreach (var update in RunTurnAsync(messages, current, cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunTurnAsync(
        IEnumerable<ChatMessage> messages,
        AcpAgentSession current,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var started = _timeProvider.GetTimestamp();
        using var activity = AcpClientDiagnostics.ActivitySource.StartActivity("acp.client.turn");
        using var turn = new PromptTurn(
            current,
            new AcpUpdateMapper(Guid.CreateVersion7().ToString("N"), _logger),
            cancellationToken,
            _lifetime.Token
        );
        Volatile.Write(ref _runningTurn, turn);
        try
        {
            var connection = await PrepareSessionAsync(turn).ConfigureAwait(false);
            var blocks = AcpPromptBuilder.Build(messages, connection.Initialize.AgentCapabilities.PromptCapabilities);
            await foreach (var update in StreamPromptAsync(connection, turn, blocks).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            Volatile.Write(ref _runningTurn, null);
            if (
                (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
                && turn.Outcome != TurnOutcome.Completed
            )
                turn.Outcome = TurnOutcome.Interrupted;
            LogTurn(activity, turn, _timeProvider.GetElapsedTime(started));
        }
    }

    private async IAsyncEnumerable<AgentResponseUpdate> StreamPromptAsync(
        AcpConnection connection,
        PromptTurn turn,
        IReadOnlyList<AcpContentBlock> blocks
    )
    {
        turn.Token.ThrowIfCancellationRequested();
        turn.AcceptsUpdates = true;
        var response = SendPromptAsync(connection, turn, blocks);
        try
        {
            await foreach (var update in turn.Output.Reader.ReadAllAsync(turn.Token).ConfigureAwait(false))
            {
                turn.Token.ThrowIfCancellationRequested();
                yield return update;
            }
            turn.StopReason = await ReadStopReasonAsync(response, turn).ConfigureAwait(false);
            turn.Token.ThrowIfCancellationRequested();
            foreach (var update in turn.Complete())
                yield return update;
        }
        finally
        {
            await turn.CancelAsync().ConfigureAwait(false);
            if (!response.IsCompleted)
                await CancelPromptAsync(connection, turn, response).ConfigureAwait(false);
            else
                await ((Task)response).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            EndPrompt(turn);
        }
    }

    private async Task<AcpConnection> PrepareSessionAsync(PromptTurn turn)
    {
        try
        {
            var connection = await GetConnectionAsync(turn.Token).ConfigureAwait(false);
            await EnsureSessionAsync(connection, turn.Session, turn.Token).ConfigureAwait(false);
            return connection;
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.AcpAgentAuthenticationRequired.Code)
        {
            turn.Outcome = TurnOutcome.AuthRequired;
            throw;
        }
    }

    private async Task<AcpConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsFaulted: false } active)
            return active;
        if (_connection is { } failed)
        {
            _connection = null;
            _openSessions.Clear();
            await failed.DisposeAsync().ConfigureAwait(false);
        }

        var spec = _launcher.Resolve(_runtime.Settings);
        var connection = await AcpConnection
            .StartAsync(
                spec,
                _runtime.WorkingDirectory,
                _runtime.EnvironmentVariables,
                _runtime.Options,
                this,
                _logger,
                _timeProvider,
                ct
            )
            .ConfigureAwait(false);
        try
        {
            await AuthenticateAsync(connection, ct).ConfigureAwait(false);
            _connection = connection;
            return connection;
        }
        finally
        {
            if (_connection is null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task AuthenticateAsync(AcpConnection connection, CancellationToken ct)
    {
        if (_runtime.Settings.AuthMethodId is not { } methodId)
            return;
        var usable = (connection.Initialize.AuthMethods ?? [])
            .Where(method => method.Type is null or "agent")
            .ToArray();
        if (!usable.Any(method => method.Id == methodId))
            throw Invalid(
                $"ACP authentication method '{methodId}' is unavailable. Available methods: {AcpAuthMethod.Describe(usable)}."
            );
        await connection
            .InvokeAsync<object?>(
                "authenticate",
                new AcpAuthenticateRequest(methodId),
                _runtime.Options.RequestTimeout,
                ct
            )
            .ConfigureAwait(false);
    }

    private async Task EnsureSessionAsync(AcpConnection connection, AcpAgentSession session, CancellationToken ct)
    {
        if (ReferenceEquals(session.Connection, connection))
        {
            if (!session.SettingsApplied)
                await ApplySettingsAsync(connection, session, ct).ConfigureAwait(false);
            return;
        }

        using var activity = AcpClientDiagnostics.ActivitySource.StartActivity("acp.client.session");
        activity?.SetTag("AgentId", _runtime.AgentId);
        var capabilities = connection.Initialize.AgentCapabilities;
        var additional = capabilities.SessionCapabilities?.AdditionalDirectories is not null
            ? _runtime.AdditionalDirectories
            : null;
        var knownId = session.SessionId ?? _runtime.ProviderSessionId;
        var restored = knownId is null
            ? null
            : await RestoreSessionAsync(connection, knownId, additional, ct).ConfigureAwait(false);
        var (id, state, source) = restored is { } previous
            ? (knownId!, previous.State, previous.Source)
            : await CreateRemoteSessionAsync(connection, additional, ct).ConfigureAwait(false);
        session.SessionId = id;
        session.Source = source;
        session.Modes = state.Modes;
        session.ConfigOptions = state.ConfigOptions;
        session.Connection = connection;
        session.SettingsApplied = false;
        _openSessions.Add(session.SessionId);
        activity?.SetTag("SessionId", session.SessionId).SetTag("SessionSource", session.Source);
        await ApplySettingsAsync(connection, session, ct).ConfigureAwait(false);
    }

    private async Task<(string Id, AcpSessionStateResponse State, string Source)> CreateRemoteSessionAsync(
        AcpConnection connection,
        IReadOnlyList<string>? additional,
        CancellationToken ct
    )
    {
        var created = await connection
            .InvokeAsync<AcpNewSessionResponse>(
                "session/new",
                new AcpNewSessionRequest(_runtime.WorkingDirectory, [], additional),
                _runtime.Options.RequestTimeout,
                ct
            )
            .ConfigureAwait(false);
        if (created is null || !AcpAgentSession.IsValidId(created.SessionId))
            throw new AgwException(
                ErrorCodes.AcpAgentRequestFailed,
                $"ACP session ID must contain between 1 and {AcpAgentSession.MaximumSessionIdLength} characters."
            );
        _openSessions.Add(created.SessionId);
        if (_runtime.OnSessionStartedAsync is { } callback)
            await callback(created.SessionId, ct).ConfigureAwait(false);
        return (created.SessionId, created, "new");
    }

    private async Task<(AcpSessionStateResponse State, string Source)?> RestoreSessionAsync(
        AcpConnection connection,
        string sessionId,
        IReadOnlyList<string>? additionalDirectories,
        CancellationToken ct
    )
    {
        var capabilities = connection.Initialize.AgentCapabilities;
        try
        {
            if (capabilities.SessionCapabilities?.Resume is not null)
            {
                var state = await connection
                    .InvokeAsync<AcpSessionStateResponse>(
                        "session/resume",
                        new AcpResumeSessionRequest(sessionId, _runtime.WorkingDirectory, [], additionalDirectories),
                        _runtime.Options.RequestTimeout,
                        ct
                    )
                    .ConfigureAwait(false);
                return (state, "resume");
            }
            if (capabilities.LoadSession)
                return (
                    await LoadSessionAsync(connection, sessionId, additionalDirectories, ct).ConfigureAwait(false),
                    "load"
                );
            _logger.LogInformation(
                "ACP agent {AgentId} cannot restore session {SessionId}",
                _runtime.AgentId,
                sessionId
            );
        }
        catch (AgwException exception)
            when (exception.InnerException is AcpRpcException { Code: AcpRpcErrorCodes.ResourceNotFound })
        {
            _logger.LogWarning(
                exception,
                "ACP agent {AgentId} could not find session {SessionId}",
                _runtime.AgentId,
                sessionId
            );
        }
        return null;
    }

    private async Task<AcpSessionStateResponse> LoadSessionAsync(
        AcpConnection connection,
        string sessionId,
        IReadOnlyList<string>? additionalDirectories,
        CancellationToken ct
    )
    {
        using var timeout = new CancellationTokenSource(_runtime.Options.RequestTimeout, _timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            // 回放期间没有活动 prompt。The ordered load response drains replay notifications before a prompt can start.
            return await connection
                .InvokeOrderedAsync<AcpSessionStateResponse>(
                    "session/load",
                    new AcpLoadSessionRequest(sessionId, _runtime.WorkingDirectory, [], additionalDirectories),
                    deadline.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "ACP agent {AgentId} timed out loading session {SessionId}",
                _runtime.AgentId,
                sessionId
            );
            throw new AgwException(
                ErrorCodes.AcpAgentTimeout,
                "The ACP agent did not respond to session/load in time.",
                exception
            );
        }
    }

    private async Task ApplySettingsAsync(AcpConnection connection, AcpAgentSession session, CancellationToken ct)
    {
        if (_runtime.Settings.ModeId is { } modeId)
        {
            var modes = session.Modes ?? throw Invalid("This ACP agent does not report session modes.");
            if (!modes.AvailableModes.Any(mode => mode.Id == modeId))
                throw Invalid(
                    $"ACP mode '{modeId}' is unavailable. Available modes: {string.Join(", ", modes.AvailableModes.Select(mode => mode.Id))}."
                );
            if (modeId != modes.CurrentModeId)
            {
                await connection
                    .InvokeAsync<object?>(
                        "session/set_mode",
                        new AcpSetModeRequest(session.SessionId!, modeId),
                        _runtime.Options.RequestTimeout,
                        ct
                    )
                    .ConfigureAwait(false);
                session.Modes = modes with { CurrentModeId = modeId };
            }
        }

        foreach (var (id, value) in _runtime.Settings.ConfigOptions?.AsEnumerable() ?? [])
        {
            var available = session.ConfigOptions ?? [];
            var option =
                available.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw Invalid(
                    $"ACP configuration option '{id}' is unavailable. Available options: {string.Join(", ", available.Select(candidate => candidate.Id))}."
                );
            var requested = ResolveConfigValue(option, value);
            if (
                option.CurrentValue.ValueKind != JsonValueKind.Undefined
                && JsonElement.DeepEquals(option.CurrentValue, requested)
            )
                continue;
            var response = await connection
                .InvokeAsync<AcpSetConfigOptionResponse>(
                    "session/set_config_option",
                    new AcpSetConfigOptionRequest(session.SessionId!, id, requested),
                    _runtime.Options.RequestTimeout,
                    ct
                )
                .ConfigureAwait(false);
            session.ConfigOptions = response.ConfigOptions;
        }
        session.SettingsApplied = true;
    }

    private static JsonElement ResolveConfigValue(AcpSessionConfigOption option, AcpConfigOptionValue value)
    {
        if (option.Type == "boolean")
        {
            if (value.Boolean is { } boolean)
                return JsonSerializer.SerializeToElement(boolean);
            throw Invalid($"ACP configuration option '{option.Id}' requires a boolean. Available values: true, false.");
        }

        var values = ReadSelectValues(option.Options).ToArray();
        if (option.Type == "select" && value.Text is { } text && values.Contains(text, StringComparer.Ordinal))
            return JsonSerializer.SerializeToElement(text);
        throw Invalid(
            $"ACP configuration option '{option.Id}' has an unsupported value. Available values: {string.Join(", ", values)}."
        );
    }

    private static IEnumerable<string> ReadSelectValues(JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Array } array)
            yield break;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                yield return value.GetString()!;
            if (item.TryGetProperty("options", out var group))
                foreach (var nested in ReadSelectValues(group))
                    yield return nested;
        }
    }

    private async Task<AcpPromptResponse> SendPromptAsync(
        AcpConnection connection,
        PromptTurn prompt,
        IReadOnlyList<AcpContentBlock> blocks
    )
    {
        try
        {
            return await connection
                .InvokeOrderedAsync<AcpPromptResponse>(
                    "session/prompt",
                    new AcpPromptRequest(prompt.Session.SessionId!, blocks),
                    CancellationToken.None,
                    () => EndPrompt(prompt)
                )
                .ConfigureAwait(false);
        }
        finally
        {
            EndPrompt(prompt);
        }
    }

    private static async Task<string> ReadStopReasonAsync(Task<AcpPromptResponse> response, PromptTurn turn)
    {
        try
        {
            return (await response.ConfigureAwait(false))?.StopReason ?? "unknown";
        }
        catch (AgwException exception) when (exception.Code == ErrorCodes.AcpAgentAuthenticationRequired.Code)
        {
            turn.Outcome = TurnOutcome.AuthRequired;
            throw;
        }
    }

    private async Task CancelPromptAsync(AcpConnection connection, PromptTurn prompt, Task<AcpPromptResponse> response)
    {
        using var grace = new CancellationTokenSource(_runtime.Options.CancelGracePeriod, _timeProvider);
        try
        {
            await connection
                .NotifyAsync("session/cancel", new AcpCancelNotification(prompt.Session.SessionId!))
                .WaitAsync(grace.Token)
                .ConfigureAwait(false);
            await response.WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (grace.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "ACP session {SessionId} exceeded the cancellation grace period",
                prompt.Session.SessionId
            );
            await connection.KillAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (connection.IsFaulted || exception is AgwException)
        {
            _logger.LogWarning(
                exception,
                "ACP session {SessionId} ended while its prompt was being cancelled",
                prompt.Session.SessionId
            );
        }

        // 结束进程后观察 prompt 的完成状态。Observe the prompt task after terminating the process.
        await ((Task)response).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static void EndPrompt(PromptTurn prompt)
    {
        prompt.AcceptsUpdates = false;
        prompt.Output.Writer.TryComplete();
    }

    public void OnSessionUpdate(AcpSessionNotification notification)
    {
        var prompt = Volatile.Read(ref _runningTurn);
        if (
            prompt is not { AcceptsUpdates: true }
            || prompt.Session.SessionId != notification.SessionId
            || prompt.Token.IsCancellationRequested
        )
            return;
        if (notification.Update is AcpCurrentModeUpdate mode && prompt.Session.Modes is { } modes)
            prompt.Session.Modes = modes with { CurrentModeId = mode.CurrentModeId };
        if (notification.Update is AcpConfigOptionUpdate options)
            prompt.Session.ConfigOptions = options.ConfigOptions;
        prompt.Publish(prompt.Mapper.Map(notification.Update));
    }

    public async Task<AcpRequestPermissionResponse> OnRequestPermissionAsync(
        AcpRequestPermissionRequest request,
        CancellationToken ct
    )
    {
        var prompt = Volatile.Read(ref _runningTurn);
        if (prompt is not null)
            Interlocked.Increment(ref prompt.PermissionRequestCount);
        if (prompt is not { AcceptsUpdates: true } || prompt.Session.SessionId != request.SessionId)
            return _permissions.Respond(request.Options, AcpPermissionDecision.Rejected);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, prompt.Token);
        if (linked.IsCancellationRequested)
            return _permissions.Respond(request.Options, AcpPermissionDecision.Cancelled);
        prompt.Publish(prompt.Mapper.EnsureToolCall(request.ToolCall));
        var call = prompt.Mapper.GetToolCall(request.ToolCall.ToolCallId);
        var decision = await _permissions
            .DecideAsync(call?.Kind ?? "other", call?.Title, call?.RawInput, request.ToolCall.ToolCallId, linked.Token)
            .ConfigureAwait(false);
        return _permissions.Respond(request.Options, decision);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _runGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = _connection;
            if (
                connection is { IsFaulted: false }
                && connection.Initialize.AgentCapabilities.SessionCapabilities?.Close is not null
            )
                await CloseSessionsAsync(connection).ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
            _runGate.Release();
        }
    }

    private async Task CloseSessionsAsync(AcpConnection connection)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout, _timeProvider);
        try
        {
            foreach (var id in _openSessions)
                await connection
                    .InvokeAsync<object?>("session/close", new AcpCloseSessionRequest(id), CloseTimeout, timeout.Token)
                    .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AgwException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "ACP agent {AgentId} ended before closing all sessions", _runtime.AgentId);
        }
    }

    private void LogTurn(Activity? activity, PromptTurn prompt, TimeSpan elapsed)
    {
        var info = _connection?.Initialize.AgentInfo;
        var session = prompt.Session;
        var stopReason = prompt.StopReason;
        var outcome = AcpClientDiagnostics.OutcomeName(prompt.Outcome);
        var tags = new TagList { { "outcome", outcome } };
        AcpClientDiagnostics.Turns.Add(1, tags);
        AcpClientDiagnostics.TurnDuration.Record(elapsed.TotalSeconds, tags);
        activity
            ?.SetTag("AgentId", _runtime.AgentId)
            .SetTag("AcpAgentName", info?.Name)
            .SetTag("AcpAgentVersion", info?.Version)
            .SetTag("SessionId", session.SessionId)
            .SetTag("SessionSource", session.Source)
            .SetTag("StopReason", stopReason)
            .SetTag("ToolCallCount", prompt.ToolCallCount)
            .SetTag("PermissionRequestCount", prompt.PermissionRequestCount)
            .SetTag("ElapsedMs", elapsed.TotalMilliseconds)
            .SetTag("Outcome", outcome);
        _logger.LogInformation(
            "ACP turn {Outcome} for {AgentId} ({AcpAgentName} {AcpAgentVersion}), session {SessionId} ({SessionSource}), stop {StopReason}, {ToolCallCount} tool calls, {PermissionRequestCount} permission requests, {ElapsedMs} ms",
            outcome,
            _runtime.AgentId,
            info?.Name,
            info?.Version,
            session.SessionId,
            session.Source,
            stopReason,
            prompt.ToolCallCount,
            prompt.PermissionRequestCount,
            elapsed.TotalMilliseconds
        );
    }

    private static AcpAgentSession RequireSession(AgentSession session) =>
        session as AcpAgentSession ?? throw Invalid("The session is not an ACP session.");

    private static AgwException Invalid(string message) => new(ErrorCodes.InvalidParam, message);

    private sealed class PromptTurn : IDisposable
    {
        private readonly CancellationTokenSource _stop;

        public PromptTurn(
            AcpAgentSession session,
            AcpUpdateMapper mapper,
            CancellationToken cancellationToken,
            CancellationToken lifetime
        )
        {
            Session = session;
            Mapper = mapper;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            Token = _stop.Token;
        }

        public AcpAgentSession Session { get; }
        public AcpUpdateMapper Mapper { get; }
        public CancellationToken Token { get; }
        public Channel<AgentResponseUpdate> Output { get; } =
            Channel.CreateUnbounded<AgentResponseUpdate>(new UnboundedChannelOptions { SingleReader = true });
        public int ToolCallCount { get; private set; }
        public int PermissionRequestCount;
        public volatile bool AcceptsUpdates;
        public TurnOutcome Outcome { get; set; } = TurnOutcome.Failed;
        public string? StopReason { get; set; }

        public IEnumerable<AgentResponseUpdate> Complete()
        {
            foreach (var update in Mapper.Complete())
            {
                Token.ThrowIfCancellationRequested();
                RecordToolCalls(update);
                yield return update;
            }
            if (StopReason != AcpStopReasons.EndTurn)
            {
                Outcome = TurnOutcome.Stopped;
                throw new AgwException(
                    ErrorCodes.AcpAgentTurnStopped,
                    $"The ACP agent stopped the turn: {StopReason}."
                );
            }
            if (Mapper.CreateResult() is { } result)
            {
                Token.ThrowIfCancellationRequested();
                yield return result;
            }
            Outcome = TurnOutcome.Completed;
        }

        public void Publish(IReadOnlyList<AgentResponseUpdate> updates)
        {
            foreach (var update in updates)
            {
                RecordToolCalls(update);
                Output.Writer.TryWrite(update);
            }
        }

        public void RecordToolCalls(AgentResponseUpdate update) =>
            ToolCallCount += update.Contents.Count(content => content is FunctionCallContent);

        public Task CancelAsync() => _stop.CancelAsync();

        public void Dispose() => _stop.Dispose();
    }
}
