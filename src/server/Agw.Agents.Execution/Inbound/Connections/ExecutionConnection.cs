using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Inbound.Connections;

internal sealed class ExecutionConnection : IAsyncDisposable
{
    private readonly string _connectionId;
    private readonly AsyncServiceScope _scope;
    private readonly ExecutionCommandDispatcher _dispatcher;
    private readonly ExecutionConnectionContext _context;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly ILogger _logger;
    private volatile bool _attached = true;
    private int _disposed;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExecutionConnection(
        string connectionId,
        string userId,
        AsyncServiceScope scope,
        ExecutionCommandDispatcher dispatcher,
        ExecutionConnectionContext context,
        ILogger logger
    )
    {
        _connectionId = connectionId;
        UserId = string.IsNullOrWhiteSpace(userId)
            ? throw new AgwException(ErrorCodes.AuthenticationRequired)
            : userId.Trim();
        _scope = scope;
        _dispatcher = dispatcher;
        _context = context;
        _logger = logger;
    }

    public string UserId { get; }

    internal bool IsAttached => _attached;

    public async Task DispatchAsync(AgentRunCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _dispatcher.DispatchAsync(command, _context, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<IReadOnlyList<AgentflowCheckpointAvailability>> GetAgentflowCheckpointsAsync(
        Guid agentflowId,
        CancellationToken cancellationToken
    )
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await _context.GetAgentflowCheckpointsAsync(agentflowId, cancellationToken);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    internal async Task<bool> IsActiveConversationAsync(
        Guid projectId,
        string contextId,
        CancellationToken cancellationToken
    )
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            return Volatile.Read(ref _disposed) == 0
                && _context.ProjectId == projectId
                && string.Equals(_context.ContextId, contextId, StringComparison.Ordinal)
                && !_context.WhenIdleAsync().IsCompleted;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>确认旧连接执行是否仍在运行，并允许同一用户在重连后请求停止。</summary>
    internal async Task<bool> RecoverInProcessExecutionAsync(bool interrupt, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return !_disposeCompletion.Task.IsCompleted;

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return !_disposeCompletion.Task.IsCompleted;
            if (interrupt && _context.HasActiveTurn)
                await _context.InterruptTurnAsync(null, cancellationToken);
            return !_context.WhenIdleAsync().IsCompleted;
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
            hasActiveTurn = _context.PrepareForDetach();
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

        await _commandGate.WaitAsync(CancellationToken.None);
        try
        {
            _attached = false;
            await _context.DisposeAsync();
        }
        finally
        {
            _commandGate.Release();
        }

        // Recovery calls may already be queued; the managed gate is collected with this connection.
        await _scope.DisposeAsync();
        _disposeCompletion.TrySetResult();
    }

    private async Task DisposeWhenIdleAsync(Action remove)
    {
        try
        {
            await _context.WhenIdleAsync();
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
            _logger.LogError(exception, "Failed to dispose execution connection {ConnectionId}.", _connectionId);
        }
        finally
        {
            remove();
        }
    }
}
