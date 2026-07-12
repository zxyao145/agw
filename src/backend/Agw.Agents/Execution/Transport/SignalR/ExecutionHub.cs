using Agw.Agents.Execution.Contracts;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Agw.Agents.Execution.Transport.SignalR;

[Authorize]
public sealed class ExecutionHub : Hub<IExecutionHubClient>
{
    private readonly ExecutionConnectionRegistry _registry;

    public ExecutionHub(ExecutionConnectionRegistry registry)
    {
        _registry = registry;
    }

    public override Task OnConnectedAsync()
    {
        _registry.Connect(Context.ConnectionId, CurrentUser);
        return base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _registry.DisconnectAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task DispatchCommand(AgentRunCommand command)
    {
        return InvokeAsync(() => _registry.DispatchAsync(
            Context.ConnectionId,
            CurrentUser,
            command,
            Context.ConnectionAborted));
    }

    private string CurrentUser => Context.User?.Identity?.Name ?? "system";

    private static async Task InvokeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AgwException exception)
        {
            throw CreateHubException(exception);
        }
    }

    private static HubException CreateHubException(AgwException exception) =>
        new($"{exception.Code}: {exception.Message}");
}
