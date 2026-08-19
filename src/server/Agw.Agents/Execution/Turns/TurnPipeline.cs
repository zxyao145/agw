using Agw.Agents.Execution.Messaging;
using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Execution.Turns;

public static class TurnPipeline
{
    public static async Task RunAsync(
        IAsyncEnumerable<AgwMessage> messages,
        bool stream,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken
    )
    {
        await sink.WriteAsync(TurnMessageFactory.CreateStarted(), CancellationToken.None);
        var bufferedMessages = new List<AgwMessage>();
        var status = "completed";
        var fatalErrorReceived = false;

        try
        {
            await foreach (var message in messages.WithCancellation(cancellationToken))
            {
                var messageType = GetMessageType(message);
                if (string.Equals(messageType, "turn-finished", StringComparison.Ordinal))
                {
                    continue;
                }

                var isFatalError = IsFatalError(message);
                if (isFatalError)
                {
                    fatalErrorReceived = true;
                    status = "failed";

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

                if (stream || IsControlMessage(messageType))
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
            status = fatalErrorReceived ? "failed" : "interrupted";
        }
        catch (Exception exception)
        {
            status = "failed";
            if (!fatalErrorReceived)
            {
                await sink.WriteAsync(CreateErrorMessage(exception.Message), CancellationToken.None);
            }
        }
        finally
        {
            await sink.WriteAsync(TurnMessageFactory.CreateFinished(status), CancellationToken.None);
        }
    }

    private static bool IsControlMessage(string? messageType) =>
        messageType?.StartsWith("human-gate-", StringComparison.Ordinal) == true
        || messageType?.StartsWith("tool-approval-", StringComparison.Ordinal) == true
        || string.Equals(messageType, "agentflow-checkpoint", StringComparison.Ordinal);

    private static bool IsFatalError(AgwMessage message) =>
        message
            .Contents.OfType<AgwErrorContent>()
            .Any(content =>
                content.AdditionalProperties?.TryGetValue("isFatalError", out var value) == true && value is true
            );

    private static string? GetMessageType(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true ? value as string : null;

    private static AgwMessage CreateErrorMessage(string message) =>
        new(
            Guid.CreateVersion7().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = message }]
        );
}
