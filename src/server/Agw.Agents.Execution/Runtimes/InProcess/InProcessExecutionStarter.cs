using Agw.Agents.Execution.HumanInteraction.Contracts;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Outbound;
using Agw.Agents.Execution.Runtimes.Contracts;
using Agw.Agents.Execution.Turns;

namespace Agw.Agents.Execution.Runtimes.InProcess;

/// <summary>
/// 连接内的进程内启动适配器，持有并复用目标 Runtime。
/// </summary>
internal sealed class InProcessExecutionStarter : IExecutionStarter
{
    private readonly IRuntimeFactory _runtimeFactory;
    private readonly string _userId;
    private readonly IExecutionMessageSink _messageSink;
    private readonly CancellationToken _hostToken;
    private readonly Action<HumanGateApprovalRequest?> _pendingHumanGateChanged;
    private ExecutionTarget? _target;

    public InProcessExecutionStarter(
        IRuntimeFactory runtimeFactory,
        string userId,
        IExecutionMessageSink messageSink,
        CancellationToken hostToken,
        Action<HumanGateApprovalRequest?> pendingHumanGateChanged
    )
    {
        _runtimeFactory = runtimeFactory;
        _userId = userId;
        _messageSink = messageSink;
        _hostToken = hostToken;
        _pendingHumanGateChanged = pendingHumanGateChanged;
    }

    // 供现有连接控制路径处理中断、模式和 checkpoint；不进入公共启动契约。
    internal RuntimeBase? Runtime { get; private set; }

    public async Task<ExecutionReceipt> StartAsync(ExecutionStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_target.HasValue && _target.Value != request.Target)
        {
            await ReleaseRuntimeAsync();
        }

        var context = new RuntimeTurnContext(
            request.Settings,
            request.Task,
            request.Target,
            request.Workspace,
            _messageSink,
            _pendingHumanGateChanged
        )
        {
            UserId = _userId,
        };
        var start = await _runtimeFactory.StartAsync(
            new RuntimeStartRequest(
                request.Target.AgentId,
                request.Task,
                ExecutionStartCommandMapper.Map(request),
                Runtime,
                context
            )
            {
                RequestedMode = request.RequestedMode,
            },
            _hostToken
        );
        Runtime = start.Runtime;
        _target = Runtime == null ? null : request.Target;
        return new ExecutionReceipt(request.ExecutionId, start.ActiveTurn != null);
    }

    internal async Task ReleaseRuntimeAsync()
    {
        if (Runtime != null)
        {
            await Runtime.DisposeAsync();
            Runtime = null;
        }

        _target = null;
        _pendingHumanGateChanged(null);
    }
}
