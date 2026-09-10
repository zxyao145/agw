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
internal sealed class ExecutionConnectionContextFactory
{
    private readonly IRuntimeFactory _runtimeFactory;
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
        IRuntimeFactory runtimeFactory,
        IProjectTaskFacade projectTasks,
        IProjectRuntimeFacade projects,
        IProjectDefaultResolver projectDefaults,
        IOptions<ExecutionRuntimeOptions> executionOptions,
        IServiceProvider serviceProvider
    )
    {
        _permissions = serviceProvider.GetRequiredService<ExecutionPermissionService>();
        _runtimeFactory = runtimeFactory;
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
        return new ExecutionConnectionContext(
            userId,
            messageSink,
            hostToken,
            _runtimeFactory,
            _projectTasks,
            _projects,
            durableSession,
            _checkpointStore,
            _projectDefaults,
            _permissions
        );
    }
}
