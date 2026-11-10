using Agw.Agents.Execution.Steering;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using PiAgentSdk;
using PiAgentSdk.MAF;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Pi;

/// <summary>
/// 通过 Pi RPC 的 steer 命令把输入交给本次运行；Pi 在当前这批工具调用结束之后、下一次模型调用之前送达。
/// 第一段输出出现时运行已经受理；Pi 拒绝或运行已结束时，本次运行不再接收，输入留给同一个 Turn 的下一次运行。
/// Hands inputs to this run through the Pi RPC steer command; Pi delivers them after the current batch of tool calls and before the next model call.
/// The run is accepted once its first output appears; when Pi refuses or the run has settled, this run takes no more input and the inputs are left for the next run of the same turn.
/// </summary>
internal sealed class PiSteerTarget : IExternalSteerTarget
{
    private readonly PiAgentAIAgent _agent;
    private readonly AgentSession _session;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopped;

    public PiSteerTarget(PiAgentAIAgent agent, AgentSession session, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);
        _agent = agent;
        _session = session;
        _logger = logger;
    }

    public void Observe(AgentResponseUpdate update) => _started.TrySetResult();

    public Task WhenReadyAsync(CancellationToken cancellationToken) =>
        (_stopped ? _never.Task : _started.Task).WaitAsync(cancellationToken);

    public async Task<ExternalSteerResult> SteerAsync(SteerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool accepted;
        try
        {
            accepted = await _agent
                .SteerAsync(_session, [SteerDelivery.CreateModelMessage(entry)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PiRpcException exception)
        {
            // Pi 拒绝排队的输入（例如扩展命令）作为下一次运行的提示送达。
            // An input Pi refuses to queue (such as an extension command) is delivered as the prompt of the next run.
            _logger.LogInformation(exception, "Pi refused steering input {SteerId}.", entry.SteerId);
            accepted = false;
        }
        if (accepted)
            return ExternalSteerResult.Delivered;
        _stopped = true;
        return ExternalSteerResult.Retry;
    }
}
