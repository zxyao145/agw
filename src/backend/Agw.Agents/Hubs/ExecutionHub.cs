using Agw.Agents.Application.Execution;
using Agw.Agents.Contracts;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Agw.Agents.Hubs;

[Authorize]
public sealed class ExecutionHub : Hub<IExecutionHubClient>
{
    private readonly HubExecutionConnectionRegistry _registry;

    public ExecutionHub(HubExecutionConnectionRegistry registry)
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
