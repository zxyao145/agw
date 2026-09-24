using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Outbound;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// Turn 的输出阶段：非流式时缓冲普通消息、即时转发控制消息；结束时先写入 Turn 行的结局，再写出结束消息。开始消息由受理写出。
/// The output stage of a turn: without streaming ordinary messages are buffered while control messages pass through at once; at the end the turn row's outcome is written before the finish message. Acceptance writes the start message.
/// </summary>
internal static class TurnPipeline
{
    public static async Task RunAsync(
        TurnEnvelope envelope,
        ExecutionScope scope,
        IAsyncEnumerable<AgwMessage> messages,
        bool stream,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(scope);
        var bufferedMessages = new List<AgwMessage>();
        var status = AgwTurnStatus.Completed;
        var fatalErrorReceived = false;

        try
        {
            await foreach (var message in messages.WithCancellation(cancellationToken))
            {
                if (AgwMessageClassifier.IsTurnFinished(message))
                {
                    continue;
                }

                var isFatalError = IsFatalError(message);
                if (isFatalError)
                {
                    fatalErrorReceived = true;
                    status = AgwTurnStatus.Failed;

                    if (!stream)
                    {
                        foreach (var bufferedMessage in bufferedMessages)
                        {
                            await sink.WriteAsync(bufferedMessage, CancellationToken.None);
                        }

                        bufferedMessages.Clear();
                        await sink.WriteAsync(message, CancellationToken.None);
                        continue;
                    }
                }

                if (stream || IsForwardedImmediately(message))
                {
                    await sink.WriteAsync(message, cancellationToken);
                }
                else
                {
                    bufferedMessages.Add(message);
                }
            }

            foreach (var message in bufferedMessages)
            {
                await sink.WriteAsync(message, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = fatalErrorReceived ? AgwTurnStatus.Failed : AgwTurnStatus.Interrupted;
        }
        catch (Exception exception)
        {
            status = AgwTurnStatus.Failed;
            scope.RecordFailure(exception);
            if (!fatalErrorReceived)
            {
                await sink.WriteAsync(CreateErrorMessage(exception.Message), CancellationToken.None);
            }
        }
        finally
        {
            var stepCount = scope.Outcome?.StepCount ?? 0;
            var errorCode = TurnMessageFactory.GetErrorCode(status, scope.Failure);
            try
            {
                if (scope.Turn is { } turn)
                    await turn.FinishAsync(
                        TurnRecord.ToTerminalStatus(status),
                        stepCount,
                        errorCode,
                        CancellationToken.None
                    );
            }
            finally
            {
                await sink.WriteAsync(
                    TurnMessageFactory.CreateFinished(envelope, status, stepCount, errorCode),
                    CancellationToken.None
                );
            }
        }
    }

    /// <summary>
    /// 非流式输出中立即转发的消息：控制消息。
    /// Messages forwarded at once in non-streaming output: control messages.
    /// </summary>
    internal static bool IsForwardedImmediately(AgwMessage message) => AgwMessageClassifier.IsControl(message);

    private static bool IsFatalError(AgwMessage message) =>
        message
            .Contents.OfType<AgwErrorContent>()
            .Any(content =>
                content.AdditionalProperties?.TryGetValue("isFatalError", out var value) == true && value is true
            );

    private static AgwMessage CreateErrorMessage(string message) =>
        new(
            Guid.CreateVersion7().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = message }]
        );
}
