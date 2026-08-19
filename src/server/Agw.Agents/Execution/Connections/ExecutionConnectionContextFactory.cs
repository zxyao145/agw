using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Execution.Connections;

/// <summary>
/// 根据全局执行提供程序，为 SignalR 连接创建进程内或 durable 执行上下文。
/// </summary>
internal sealed class ExecutionConnectionContextFactory
{
    private readonly IRuntimeFactory _runtimeFactory;
    private readonly ITaskAppService _taskAppService;
    private readonly IProjectAppService _projectAppService;
    private readonly ExecutionProvider _executionProvider;
    private readonly DurableExecutionCoordinator? _durableCoordinator;
    private readonly AgentflowCheckpointStore _checkpointStore;

    /// <summary>
    /// 初始化连接上下文工厂，并只在启用 Distributed 时解析其协调器。
    /// </summary>
    public ExecutionConnectionContextFactory(
        IRuntimeFactory runtimeFactory,
        ITaskAppService taskAppService,
        IProjectAppService projectAppService,
        IOptions<ExecutionRuntimeOptions> executionOptions,
        IServiceProvider serviceProvider
    )
    {
        _runtimeFactory = runtimeFactory;
        _taskAppService = taskAppService;
        _projectAppService = projectAppService;
        _executionProvider = executionOptions.Value.Provider;
        _durableCoordinator = serviceProvider.GetService<DurableExecutionCoordinator>();
        _checkpointStore = serviceProvider.GetRequiredService<AgentflowCheckpointStore>();
    }

    /// <summary>
    /// 创建与当前用户、消息通道及连接生命周期绑定的执行上下文。
    /// </summary>
    public ExecutionConnectionContext Create(
        string userName,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken
    )
    {
        var durableSession =
            _executionProvider == ExecutionProvider.Distributed
                ? new DurableExecutionSession(
                    userName,
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
            userName,
            messageSink,
            hostToken,
            _runtimeFactory,
            _taskAppService,
            _projectAppService,
            durableSession,
            _checkpointStore
        );
    }
}
