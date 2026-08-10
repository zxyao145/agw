using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Agents.Execution.Durable;
using Agw.Files.Exceptions;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Transport.SignalR;

[Authorize]
public sealed class ExecutionHub : Hub<IExecutionHubClient>
{
    private readonly ExecutionConnectionRegistry _registry;
    private readonly ExecutionProvider _executionProvider;

    /// <summary>
    /// 初始化执行 Hub，并缓存当前服务端启用的执行提供程序。
    /// </summary>
    public ExecutionHub(
        ExecutionConnectionRegistry registry,
        IOptions<ExecutionRuntimeOptions> executionOptions)
    {
        _registry = registry;
        _executionProvider = executionOptions.Value.Provider;
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

    /// <summary>
    /// 返回客户端恢复策略所需的执行提供程序能力标识。
    /// </summary>
    public Task<string> GetExecutionProvider() =>
        Task.FromResult(_executionProvider.ToString());

    private string CurrentUser => Context.User?.Identity?.Name ?? Constants.AdminUserName;

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
        catch (AgwFilesException exception)
        {
            throw CreateHubException(exception);
        }
    }

    private static HubException CreateHubException(AgwException exception) =>
        new($"{exception.Code}: {exception.Message}");

    private static HubException CreateHubException(AgwFilesException exception) =>
        new($"{exception.Code}: {exception.Message}");
}
