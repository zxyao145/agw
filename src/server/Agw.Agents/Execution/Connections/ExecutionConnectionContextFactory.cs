using Agw.Agents.Execution.Messaging;
using Agw.Agents.Execution.Runtimes;
using Agw.Shared.Contracts.Projects;

namespace Agw.Agents.Execution.Connections;

internal sealed class ExecutionConnectionContextFactory
{
    private readonly IRuntimeFactory _runtimeFactory;
    private readonly ITaskAppService _taskAppService;
    private readonly IProjectAppService _projectAppService;

    public ExecutionConnectionContextFactory(
        IRuntimeFactory runtimeFactory,
        ITaskAppService taskAppService,
        IProjectAppService projectAppService)
    {
        _runtimeFactory = runtimeFactory;
        _taskAppService = taskAppService;
        _projectAppService = projectAppService;
    }

    public ExecutionConnectionContext Create(
        string userName,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken) =>
        new(
            userName,
            messageSink,
            hostToken,
            _runtimeFactory,
            _taskAppService,
            _projectAppService);
}
