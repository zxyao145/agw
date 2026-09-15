using Agw.Agents.Definitions.Agents;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Runtimes.InProcess;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Inbound.Connections;

/// <summary>
/// 根据全局执行提供程序，为 SignalR 连接创建进程内或 durable 执行上下文。
/// </summary>
internal sealed class ExecutionConnectionContextFactory : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private AsyncServiceScope? _runtimeScope;
    private readonly IProjectTaskFacade _projectTasks;
    private readonly IProjectRuntimeFacade _projects;
    private readonly ExecutionProvider _executionProvider;
    private readonly DurableExecutionCoordinator? _durableCoordinator;
    private readonly AgentflowCheckpointStore _checkpointStore;
    private readonly IProjectDefaultResolver _projectDefaults;
    private readonly ExecutionPermissionService _permissions;

    /// <summary>
    /// 初始化连接上下文工厂，并只在启用 Distributed 时解析其协调器。
    /// </summary>
    public ExecutionConnectionContextFactory(
        IProjectTaskFacade projectTasks,
        IProjectRuntimeFacade projects,
        IProjectDefaultResolver projectDefaults,
        IOptions<ExecutionRuntimeOptions> executionOptions,
        IServiceProvider serviceProvider
    )
    {
        _permissions = serviceProvider.GetRequiredService<ExecutionPermissionService>();
        _serviceProvider = serviceProvider;
        _projectTasks = projectTasks;
        _projects = projects;
        _projectDefaults = projectDefaults;
        _executionProvider = executionOptions.Value.Provider;
        _durableCoordinator = serviceProvider.GetService<DurableExecutionCoordinator>();
        _checkpointStore = serviceProvider.GetRequiredService<AgentflowCheckpointStore>();
    }

    /// <summary>
    /// 创建与当前用户、消息通道及连接生命周期绑定的执行上下文。
    /// </summary>
    public ExecutionConnectionContext Create(
        string userId,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken
    )
    {
        var durableSession =
            _executionProvider == ExecutionProvider.Distributed
                ? new DurableExecutionSession(
                    userId,
                    messageSink,
                    hostToken,
                    _durableCoordinator
                        ?? throw new AgwException(
                            ErrorCodes.DurableExecutionUnavailable,
                            "Durable execution services are not configured."
                        )
                )
                : null;
        // Commands remain on the connection scope while an InProcess turn runs in the background.
        // Keep their scoped persistence services separate so both paths can query concurrently.
        if (_executionProvider == ExecutionProvider.InProcess)
        {
            _runtimeScope ??= _serviceProvider.CreateAsyncScope();
        }
        var runtimeFactory = (_runtimeScope?.ServiceProvider ?? _serviceProvider).GetRequiredService<IRuntimeFactory>();
        return new ExecutionConnectionContext(
            userId,
            messageSink,
            hostToken,
            runtimeFactory,
            _projectTasks,
            _projects,
            durableSession,
            _checkpointStore,
            _projectDefaults,
            _permissions
        );
    }

    // The connection disposes its runtime before disposing the owning DI scope and this factory.
    public async ValueTask DisposeAsync()
    {
        if (_runtimeScope is { } scope)
        {
            _runtimeScope = null;
            await scope.DisposeAsync();
        }
    }
}
