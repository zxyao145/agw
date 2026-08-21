using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Abstracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Connections;

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
        UserId = string.IsNullOrWhiteSpace(userId) ? Constants.AdminUserId : userId.Trim();
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

        _commandGate.Dispose();
        await _scope.DisposeAsync();
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
