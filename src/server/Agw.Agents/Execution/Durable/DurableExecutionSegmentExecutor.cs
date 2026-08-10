using Agw.Agents.Execution.Agentflows;
using Agw.Shared.Data;

using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 执行一个可恢复分段：加载 PostgreSQL 清单、调用 Agent 或 Agentflow，并把普通输出写入消息流。
/// </summary>
internal sealed class DurableExecutionSegmentExecutor
{
    private readonly DurableExecutionStore _store;
    private readonly DurableAgentSegmentRunner _agentRunner;
    private readonly AgentflowRuntimeService _agentflowRuntimeService;
    private readonly IExecutionEventStream _eventStream;
    private readonly ILogger<DurableExecutionSegmentExecutor> _logger;

    /// <summary>
    /// 初始化可执行 Agent 与 Agentflow 的分段执行器。
    /// </summary>
    public DurableExecutionSegmentExecutor(
        DurableExecutionStore store,
        DurableAgentSegmentRunner agentRunner,
        AgentflowRuntimeService agentflowRuntimeService,
        IExecutionEventStream eventStream,
        ILogger<DurableExecutionSegmentExecutor> logger)
    {
        _store = store;
        _agentRunner = agentRunner;
        _agentflowRuntimeService = agentflowRuntimeService;
        _eventStream = eventStream;
        _logger = logger;
    }

    /// <summary>
    /// 按持久化清单执行指定分段。pending 与 terminal 控制消息由状态落库后的协调层发布。
    /// </summary>
    public async Task<DurableExecutionSegmentResult> RunAsync(
        DurableExecutionSegmentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sink = new ExecutionStreamMessageSink(
            _eventStream,
            input.ExecutionId,
            input.SegmentIndex,
            _logger);
        var manifest = (await _store.GetAsync(input.ExecutionId, cancellationToken)
                .ConfigureAwait(false))
            .Manifest;
        return manifest.AgentType switch
        {
            AgentRuntimeType.Agent => await _agentRunner.RunAsync(
                    manifest,
                    input,
                    sink,
                    cancellationToken)
                .ConfigureAwait(false),
            AgentRuntimeType.Agentflow => await _agentflowRuntimeService.ExecuteDurableSegmentAsync(
                    manifest,
                    input,
                    sink,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => new DurableExecutionSegmentResult
            {
                ExecutionId = input.ExecutionId,
                SegmentIndex = input.SegmentIndex,
                Status = DurableExecutionSegmentStatus.Failed,
                ErrorMessage = $"Agent runtime type '{manifest.AgentType}' is not supported."
            }
        };
    }
}
