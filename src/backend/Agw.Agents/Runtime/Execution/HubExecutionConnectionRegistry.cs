using System.Collections.Concurrent;

using Agw.Agents.Runtime.Contracts;
using Agw.Agents.Runtime.Hubs;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Runtime.Execution;

public sealed class HubExecutionConnectionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ExecutionHub, IExecutionHubClient> _hubContext;
    private readonly CancellationToken _hostToken;
    private readonly ILoggerFactory _loggerFactory;

    public HubExecutionConnectionRegistry(
        IServiceScopeFactory scopeFactory,
        IHubContext<ExecutionHub, IExecutionHubClient> hubContext,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _hostToken = applicationLifetime.ApplicationStopping;
        _loggerFactory = loggerFactory;
    }

    public void Connect(string connectionId, string userName)
    {
        var entry = new ConnectionEntry(
            connectionId,
            userName,
            _scopeFactory.CreateAsyncScope(),
            _hubContext,
            _hostToken,
            _loggerFactory.CreateLogger<ConnectionEntry>());
        if (!_connections.TryAdd(connectionId, entry))
        {
            _ = entry.DisposeAsync();
        }
    }

    public Task DispatchAsync(
        string connectionId,
        string userName,
        AgentRunCommand command,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var entry)
            || !string.Equals(entry.UserName, userName, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Execution connection is not available.");
        }

        return entry.DispatchAsync(command, cancellationToken);
    }

    public Task DisconnectAsync(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return Task.CompletedTask;
        }

        return entry.DetachAsync(() => _connections.TryRemove(connectionId, out _));
    }

    public async ValueTask DisposeAsync()
    {
        var entries = _connections.Values.ToArray();
        _connections.Clear();
        foreach (var entry in entries)
        {
            await entry.DisposeAsync();
        }
    }

    private sealed class ConnectionEntry : IAsyncDisposable
    {
        private const string BusyMessage = "The previous session is currently in progress, please wait and execute again.";

        private readonly string _connectionId;
        private readonly AsyncServiceScope _scope;
        private readonly ExecutionRuntimeStarter _runtimeStarter;
        private readonly SignalRExecutionMessageSink _messageSink;
        private readonly CancellationToken _hostToken;
        private readonly SemaphoreSlim _commandGate = new(1, 1);
        private readonly ILogger _logger;
        private SettingCommand? _settings;
        private TaskProjection? _resolvedTask;
        private RuntimeExecSessionBase? _runtimeSession;
        private ExecutionTarget? _target;
        private volatile bool _waitingForHuman;
        private volatile bool _attached = true;
        private int _disposed;

        public ConnectionEntry(
            string connectionId,
            string userName,
            AsyncServiceScope scope,
            IHubContext<ExecutionHub, IExecutionHubClient> hubContext,
            CancellationToken hostToken,
            ILogger logger)
        {
            _connectionId = connectionId;
            UserName = userName;
            _scope = scope;
            _runtimeStarter = scope.ServiceProvider.GetRequiredService<ExecutionRuntimeStarter>();
            _hostToken = hostToken;
            _logger = logger;
            _messageSink = new SignalRExecutionMessageSink(
                connectionId,
                hubContext,
                () => _attached,
                logger);
        }

        public string UserName { get; }

        public async Task DispatchAsync(AgentRunCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            await _commandGate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                switch (command)
                {
                    case SettingCommand settings:
                        await ApplySettingsAsync(settings);
                        break;
                    case ExecCommand exec:
                        await ExecuteAsync(exec, cancellationToken);
                        break;
                    case InterruptCommand interrupt:
                        await InterruptAsync(interrupt);
                        break;
                    case HumanResponseCommand response:
                        await SubmitHumanResponseAsync(response, cancellationToken);
                        break;
                    default:
                        throw new AgwException(ErrorCodes.InvalidParam, "Unsupported execution command.");
                }
            }
            finally
            {
                _commandGate.Release();
            }
        }

        public async Task DetachAsync(Action remove)
        {
            await _commandGate.WaitAsync(CancellationToken.None);
            var hasActiveTurn = false;
            try
            {
                _attached = false;
                hasActiveTurn = _runtimeSession is { HasActiveTurn: true };
                if (hasActiveTurn && _waitingForHuman)
                {
                    _runtimeSession!.RequestInterrupt("SignalR connection disconnected while waiting for human input.");
                }
            }
            finally
            {
                _commandGate.Release();
            }

            if (!hasActiveTurn)
            {
                await DisposeAndRemoveAsync(remove);
                return;
            }

            _ = DisposeWhenIdleAsync(remove);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            RuntimeExecSessionBase? runtimeSession;
            await _commandGate.WaitAsync(CancellationToken.None);
            try
            {
                _attached = false;
                runtimeSession = _runtimeSession;
                _runtimeSession = null;
            }
            finally
            {
                _commandGate.Release();
            }

            if (runtimeSession != null) await runtimeSession.DisposeAsync();

            _commandGate.Dispose();
            await _scope.DisposeAsync();
        }

        private async Task ApplySettingsAsync(SettingCommand settings)
        {
            if (_runtimeSession is { HasActiveTurn: true })
            {
                await SendErrorAsync(BusyMessage);
                return;
            }

            var normalized = CloneSettings(settings);
            if (_settings == normalized)
            {
                return;
            }

            await ReplaceRuntimeSessionAsync();
            _settings = normalized;
            _resolvedTask = null;
            _target = null;
        }

        private async Task ExecuteAsync(ExecCommand command, CancellationToken cancellationToken)
        {
            if (!command.AgentId.HasValue || command.AgentId.Value == Guid.Empty)
            {
                throw new AgwException(ErrorCodes.InvalidParam, "ExecCommand.agentId is required.");
            }

            if (_runtimeSession is { HasActiveTurn: true })
            {
                await SendErrorAsync(BusyMessage);
                return;
            }

            _settings ??= CreateDefaultSettings();
            if (_resolvedTask == null)
            {
                var resolution = await _runtimeStarter.ResolveTaskAsync(
                    new ExecutionTaskRequest(
                        TaskId: null,
                        ProjectId: _settings.ProjectId,
                        ContextId: _settings.ContextId,
                        Input: AgwUserInputUtil.ExtractInputText(command.Input),
                        Resume: _settings.Resume,
                        User: UserName),
                    cancellationToken);
                _resolvedTask = resolution.Task
                    ?? throw new AgwException(ErrorCodes.InvalidParam, "Execution task could not be resolved.");
            }

            var target = new ExecutionTarget(command.AgentId.Value, command.AgentType);
            if (_target != null && _target != target)
            {
                await ReplaceRuntimeSessionAsync();
            }

            var start = await _runtimeStarter.StartAsync(
                new StreamingExecutionStartRequest(
                    target.AgentId,
                    _resolvedTask,
                    command,
                    _runtimeSession,
                    _settings,
                    _messageSink,
                    pending => _waitingForHuman = pending != null),
                _hostToken);
            _runtimeSession = start.RuntimeSession;
            _target = target;
            if (start.ActiveTurn == null)
            {
                await SendErrorAsync("Agent execution could not be started.");
            }
        }

        private async Task InterruptAsync(InterruptCommand command)
        {
            if (_runtimeSession is not { HasActiveTurn: true })
            {
                await SendSystemMessageAsync(command.Reason ?? "No active request is currently running.");
                return;
            }

            _runtimeSession.RequestInterrupt(command.Reason);
        }

        private async Task SubmitHumanResponseAsync(
            HumanResponseCommand command,
            CancellationToken cancellationToken)
        {
            if (_runtimeSession == null
                || !await _runtimeSession.TrySubmitHumanResponseAsync(command, cancellationToken))
            {
                await SendSystemMessageAsync("No matching HumanGate request is waiting for this response.");
            }
        }

        private async Task ReplaceRuntimeSessionAsync()
        {
            if (_runtimeSession == null)
            {
                return;
            }

            await _runtimeSession.DisposeAsync();
            _runtimeSession = null;
            _target = null;
            _waitingForHuman = false;
        }

        private async Task DisposeWhenIdleAsync(Action remove)
        {
            try
            {
                if (_runtimeSession != null)
                {
                    await _runtimeSession.WhenIdleAsync();
                }
            }
            finally
            {
                await DisposeAndRemoveAsync(remove);
            }
        }

        private async Task DisposeAndRemoveAsync(Action remove)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to dispose SignalR execution connection {ConnectionId}.", _connectionId);
            }
            finally
            {
                remove();
            }
        }

        private Task SendErrorAsync(string message) =>
            _messageSink.WriteAsync(CreateMessage(new AgwErrorContent { Content = message }), CancellationToken.None).AsTask();

        private Task SendSystemMessageAsync(string message) =>
            _messageSink.WriteAsync(CreateMessage(new AgwTextContent { Content = message }), CancellationToken.None).AsTask();

        private static AgwMessage CreateMessage(AgwContent content) =>
            new(
                Guid.NewGuid().ToString("D"),
                Constants.DefaultAgentAuthor,
                AiRole.System,
                [content]);

        private static SettingCommand CreateDefaultSettings() =>
            new(ProjectDefaults.DefaultBuiltInId, contextId: null);

        private static SettingCommand CloneSettings(SettingCommand settings) =>
            new(
                settings.ProjectId,
                new Dictionary<string, string>(settings.EnvironmentVariables),
                settings.ContextId)
            {
                Resume = settings.Resume,
            };

        private readonly record struct ExecutionTarget(Guid AgentId, AgentRuntimeType AgentType);
    }

    private sealed class SignalRExecutionMessageSink : IExecutionMessageSink
    {
        private readonly string _connectionId;
        private readonly IHubContext<ExecutionHub, IExecutionHubClient> _hubContext;
        private readonly Func<bool> _isAttached;
        private readonly ILogger _logger;

        public SignalRExecutionMessageSink(
            string connectionId,
            IHubContext<ExecutionHub, IExecutionHubClient> hubContext,
            Func<bool> isAttached,
            ILogger logger)
        {
            _connectionId = connectionId;
            _hubContext = hubContext;
            _isAttached = isAttached;
            _logger = logger;
        }

        public async ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken)
        {
            if (!_isAttached())
            {
                return;
            }

            try
            {
                await _hubContext.Clients.Client(_connectionId).ReceiveMessage(message);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Failed to send execution message to {ConnectionId}.", _connectionId);
            }
        }
    }
}
