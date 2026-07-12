using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Contracts;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Connections;

public sealed class ExecutionConnection : IAsyncDisposable
{
    internal const string BusyMessage = "The previous session is currently in progress, please wait and execute again.";

    private readonly string _connectionId;
    private readonly AsyncServiceScope _scope;
    private readonly ExecutionCommandDispatcher _dispatcher;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ILogger _logger;
    private volatile bool _waitingForHuman;
    private volatile bool _attached = true;
    private int _disposed;

    public ExecutionConnection(
        string connectionId,
        string userName,
        AsyncServiceScope scope,
        ExecutionCommandDispatcher dispatcher,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        ILogger logger)
    {
        _connectionId = connectionId;
        UserName = userName;
        _scope = scope;
        _dispatcher = dispatcher;
        MessageSink = messageSink;
        HostToken = hostToken;
        _logger = logger;
    }

    public string UserName { get; }

    internal SettingCommand? Settings { get; set; }

    internal TaskProjection? ResolvedTask { get; set; }

    internal RuntimeBase? Runtime { get; set; }

    internal ExecutionTarget? Target { get; set; }

    internal IExecutionMessageSink MessageSink { get; }

    internal CancellationToken HostToken { get; }

    internal bool IsAttached => _attached;

    public async Task DispatchAsync(AgentRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _dispatcher.DispatchAsync(command, this, cancellationToken);
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
            hasActiveTurn = Runtime is { HasActiveTurn: true };
            if (hasActiveTurn && _waitingForHuman)
            {
                Runtime!.RequestInterrupt();
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

        RuntimeBase? runtime;
        await _commandGate.WaitAsync(CancellationToken.None);
        try
        {
            _attached = false;
            runtime = Runtime;
            Runtime = null;
        }
        finally
        {
            _commandGate.Release();
        }

        if (runtime != null)
        {
            await runtime.DisposeAsync();
        }

        _commandGate.Dispose();
        await _scope.DisposeAsync();
    }

    internal void SetWaitingForHuman(bool waitingForHuman) =>
        _waitingForHuman = waitingForHuman;

    internal async Task ReplaceRuntimeAsync()
    {
        if (Runtime == null)
        {
            return;
        }

        await Runtime.DisposeAsync();
        Runtime = null;
        Target = null;
        _waitingForHuman = false;
    }

    internal Task SendErrorAsync(string message) =>
        MessageSink.WriteAsync(
            CreateMessage(new AgwErrorContent { Content = message }),
            CancellationToken.None).AsTask();

    internal Task SendSystemMessageAsync(string message) =>
        MessageSink.WriteAsync(
            CreateMessage(new AgwTextContent { Content = message }),
            CancellationToken.None).AsTask();

    private async Task DisposeWhenIdleAsync(Action remove)
    {
        try
        {
            if (Runtime != null)
            {
                await Runtime.WhenIdleAsync();
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
            _logger.LogError(
                exception,
                "Failed to dispose SignalR execution connection {ConnectionId}.",
                _connectionId);
        }
        finally
        {
            remove();
        }
    }

    private static AgwMessage CreateMessage(AgwContent content) =>
        new(
            Guid.NewGuid().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [content]);
}

internal readonly record struct ExecutionTarget(Guid AgentId, AgentRuntimeType AgentType);
