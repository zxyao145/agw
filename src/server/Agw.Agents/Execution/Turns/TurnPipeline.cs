using Agw.Shared.AgwMsgVm;
using Agw.Agents.Execution.Connections;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

public static class TurnPipeline
{
    public static async Task RunAsync(
        IAsyncEnumerable<AgwMessage> messages,
        bool stream,
        IExecutionMessageSink sink,
        CancellationToken cancellationToken)
    {
        await sink.WriteAsync(CreateTurnStateMessage("turn-start"), CancellationToken.None);
        var bufferedMessages = new List<AgwMessage>();
        var status = "completed";

        try
        {
            await foreach (var message in messages.WithCancellation(cancellationToken))
            {
                var messageType = GetMessageType(message);
                if (string.Equals(messageType, "turn-finished", StringComparison.Ordinal))
                {
                    continue;
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
            status = "interrupted";
        }
        catch (Exception exception)
        {
            status = "failed";
            await sink.WriteAsync(CreateErrorMessage(exception.Message), CancellationToken.None);
        }
        finally
        {
            await sink.WriteAsync(CreateTurnStateMessage("turn-finished", status), CancellationToken.None);
        }
    }

    private static bool IsControlMessage(string? messageType) =>
        messageType?.StartsWith("human-gate-", StringComparison.Ordinal) == true;

    private static string? GetMessageType(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true
            ? value as string
            : null;

    private static AgwMessage CreateTurnStateMessage(string type, string? status = null)
    {
        var properties = new AdditionalPropertiesDictionary { ["type"] = type };
        if (status != null) properties["status"] = status;

        return new AgwMessage(
            Guid.NewGuid().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "" }],
            properties);
    }

    private static AgwMessage CreateErrorMessage(string message) =>
        new(
            Guid.NewGuid().ToString("D"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = message }]);
}
