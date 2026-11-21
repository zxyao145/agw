using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agw.Agents.Execution.Context;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Steering;

/// <summary>
/// 外部引擎接收 Steer 的结果：Delivered 表示引擎已经接收；Retry 表示引擎当前的 turn 不能接收，输入放回队首等待下一次机会。
/// The result of an external engine receiving a Steer: Delivered means the engine accepted it; Retry means the engine's current turn cannot take it, so the input returns to the front for the next chance.
/// </summary>
internal enum ExternalSteerResult
{
    Delivered,
    Retry,
}

/// <summary>
/// 一次外部引擎运行中接收 Steer 的一方。Observe 从输出更新得知引擎当前的 turn；Retry 之后 WhenReadyAsync 等到引擎开始新的 turn。
/// The party that receives Steer inputs during one external engine run. Observe learns the engine's current turn from output updates; after a Retry, WhenReadyAsync waits until the engine starts a new turn.
/// </summary>
internal interface IExternalSteerTarget
{
    void Observe(AgentResponseUpdate update);

    Task WhenReadyAsync(CancellationToken cancellationToken);

    Task<ExternalSteerResult> SteerAsync(SteerEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// 让外部引擎在运行期间接收 Steer：运行期间把 Turn 的待送达输入交给引擎，引擎接收后在输出流中放入一条用户消息更新。
/// 运行结束前先停止送达任务；没有被接收的输入留在 SteerInbox 中，由 TurnExecutor 作为同一个 Turn 的下一次运行送达。
/// Lets an external engine receive Steer inputs while it runs: the turn's waiting inputs go to the engine, and each accepted input adds a user message update to the output stream.
/// The delivery task stops before the run ends; inputs not accepted stay in the SteerInbox and the TurnExecutor delivers them as the next run of the same turn.
/// </summary>
internal sealed class ExternalSteerAgent : DelegatingAIAgent
{
    private readonly Func<AgentSession, IExternalSteerTarget> _createTarget;
    private readonly ILogger _logger;

    public ExternalSteerAgent(AIAgent innerAgent, Func<AgentSession, IExternalSteerTarget> createTarget, ILogger logger)
        : base(innerAgent)
    {
        ArgumentNullException.ThrowIfNull(createTarget);
        ArgumentNullException.ThrowIfNull(logger);
        _createTarget = createTarget;
        _logger = logger;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var steers = ExecutionScope.Current?.Steers;
        if (steers == null || session == null)
        {
            await foreach (
                var update in InnerAgent
                    .RunStreamingAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false)
            )
                yield return update;
            yield break;
        }

        if (steers is not SteerInbox inbox)
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "External engines receive steering inputs only in process."
            );
        var target = _createTarget(session);
        var channel = Channel.CreateUnbounded<AgentResponseUpdate>(new UnboundedChannelOptions { SingleReader = true });
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(messages, session, options, inbox, target, channel.Writer, runCancellation.Token);
        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            await runCancellation.CancelAsync().ConfigureAwait(false);
            // 生产者的异常已经通过通道交给读取端。The producer's exception already reached the reader through the channel.
            await producer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// 读取引擎的输出并同时运行送达任务；引擎结束后先停止送达任务并等待它结束，再关闭通道，已接收输入的更新因此不会丢失。
    /// Reads the engine output while the delivery task runs; after the engine ends, the delivery task stops and is awaited before the channel closes, so updates of accepted inputs are never lost.
    /// </summary>
    private async Task ProduceAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession session,
        AgentRunOptions? options,
        SteerInbox inbox,
        IExternalSteerTarget target,
        ChannelWriter<AgentResponseUpdate> writer,
        CancellationToken cancellationToken
    )
    {
        Exception? failure = null;
        using var deliveryStop = new CancellationTokenSource();
        var delivery = DeliverAsync(inbox, target, writer, deliveryStop.Token);
        try
        {
            await foreach (
                var update in InnerAgent
                    .RunStreamingAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                target.Observe(update);
                await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await deliveryStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await delivery.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deliveryStop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            writer.TryComplete(failure);
        }
    }

    /// <summary>
    /// 有输入等待且引擎可以接收时依次交给引擎；第一次 Retry 之后的输入不再尝试，与它一起按原顺序放回队首。
    /// 交给引擎的调用不随停止信号取消，进行中的接收总能得到结果并写入通道。
    /// Hands waiting inputs to the engine in order whenever it can receive them; inputs after the first Retry are not tried and return to the front with it in their original order.
    /// The call to the engine is not cancelled by the stop signal, so an acceptance in progress always gets its result written to the channel.
    /// </summary>
    private async Task DeliverAsync(
        SteerInbox inbox,
        IExternalSteerTarget target,
        ChannelWriter<AgentResponseUpdate> writer,
        CancellationToken stop
    )
    {
        while (await inbox.WaitForPendingAsync(stop).ConfigureAwait(false))
        {
            await target.WhenReadyAsync(stop).ConfigureAwait(false);
            var taken = inbox.TakePending();
            var retry = new List<SteerEntry>();
            foreach (var entry in taken)
            {
                if (retry.Count == 0)
                {
                    var result = await target.SteerAsync(entry, CancellationToken.None).ConfigureAwait(false);
                    if (result == ExternalSteerResult.Delivered)
                    {
                        _logger.LogInformation("Steering input {SteerId} was delivered to the engine.", entry.SteerId);
                        if (!writer.TryWrite(SteerDelivery.CreateDeliveredUpdate(entry)))
                            throw new AgwException(
                                ErrorCodes.AgentExecutionFailed,
                                "The output stream closed before a delivered steering input was recorded."
                            );
                        continue;
                    }
                }
                retry.Add(entry);
            }
            inbox.Requeue(retry);
        }
    }
}
