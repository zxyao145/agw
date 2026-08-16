using System.Collections.Concurrent;

using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Commands;
using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Connections;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Transport.SignalR;

public sealed class ExecutionConnectionRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ExecutionConnection> _connections = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<ExecutionHub, IExecutionHubClient> _hubContext;
    private readonly CancellationToken _hostToken;
    private readonly ILoggerFactory _loggerFactory;

    public ExecutionConnectionRegistry(
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
        var scope = _scopeFactory.CreateAsyncScope();
        var logger = _loggerFactory.CreateLogger<ExecutionConnection>();
        ExecutionConnection? connection = null;
        var sink = new SignalRExecutionMessageSink(
            connectionId,
            _hubContext,
            () => connection?.IsAttached == true,
            logger);
        var context = scope.ServiceProvider
            .GetRequiredService<ExecutionConnectionContextFactory>()
            .Create(userName, sink, _hostToken);
        connection = new ExecutionConnection(
            connectionId,
            userName,
            scope,
            scope.ServiceProvider.GetRequiredService<ExecutionCommandDispatcher>(),
            context,
            logger);
        if (!_connections.TryAdd(connectionId, connection))
        {
            _ = connection.DisposeAsync();
        }
    }

    public Task DispatchAsync(
        string connectionId,
        string userName,
        AgentRunCommand command,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var connection)
            || !string.Equals(connection.UserName, userName, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Execution connection is not available.");
        }

        return connection.DispatchAsync(command, cancellationToken);
    }

    public Task<IReadOnlyList<AgentflowCheckpointAvailability>> GetAgentflowCheckpointsAsync(
        string connectionId,
        string userName,
        Guid agentflowId,
        CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(connectionId, out var connection)
            || !string.Equals(connection.UserName, userName, StringComparison.Ordinal))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Execution connection is not available.");
        }

        return connection.GetAgentflowCheckpointsAsync(agentflowId, cancellationToken);
    }

    public Task DisconnectAsync(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
        {
            return Task.CompletedTask;
        }

        return connection.DetachAsync(() => _connections.TryRemove(connectionId, out _));
    }

    public async ValueTask DisposeAsync()
    {
        var connections = _connections.Values.ToArray();
        _connections.Clear();
        foreach (var connection in connections)
        {
            await connection.DisposeAsync();
        }
    }
}
