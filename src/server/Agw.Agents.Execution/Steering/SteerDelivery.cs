using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Messaging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Steering;

/// <summary>
/// 送达已从 SteerInbox 取走的输入：生成模型输入消息，并向客户端写出用户消息与 steer-delivered。
/// Delivers inputs already taken from the SteerInbox: creates the model input messages and writes the user message and steer-delivered to the client.
/// </summary>
internal static class SteerDelivery
{
    /// <summary>
    /// SteerId 在模型输入消息上的属性名。
    /// The property name of the SteerId on the model input message.
    /// </summary>
    public const string SteerIdProperty = "agw.steerId";

    public static async Task<List<ChatMessage>> DeliverAsync(
        ExecutionScope scope,
        IReadOnlyList<SteerEntry> entries,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entries);
        var messages = new List<ChatMessage>(entries.Count);
        var delivered = 0;
        try
        {
            foreach (var entry in entries)
            {
                var message = CreateModelMessage(entry);
                await scope.Output.WriteAsync(SteerMessages.CreateUserMessage(scope, entry), cancellationToken);
                await scope.Output.WriteAsync(
                    SteerMessages.CreateDelivered(scope, entry, scope.StepIndex),
                    cancellationToken
                );
                messages.Add(message);
                delivered++;
            }
        }
        catch (OperationCanceledException)
        {
            // 取消发生在取走之后、写出之前：模型调用不会发生，没有送达的输入交还客户端。
            // Cancellation came after the take and before the write: the model call will not happen, so inputs not delivered go back to the client.
            await ReturnAsync(scope, entries.Skip(delivered).ToList(), AgwTurnStatus.Interrupted);
            throw;
        }
        return messages;
    }

    /// <summary>
    /// 把没有送达的输入交还客户端：写出携带原始输入的 steer-returned。
    /// Hands inputs that were not delivered back to the client by writing steer-returned with the original input.
    /// </summary>
    public static async Task ReturnAsync(ExecutionScope scope, IReadOnlyList<SteerEntry> entries, string turnStatus)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            await scope.Output.WriteAsync(
                SteerMessages.CreateReturned(scope, entry, SteerMessages.ToReturnReason(turnStatus)),
                CancellationToken.None
            );
        }
    }

    /// <summary>
    /// 校验输入能转换为模型消息；无效输入抛出 AgwException。
    /// Validates that the input converts to a model message; invalid input throws AgwException.
    /// </summary>
    public static void Validate(AgwUserInput input) => _ = AgwMessageUtil.CreateUserChatMessage(input);

    /// <summary>
    /// 外部引擎接收一条输入后放入输出流的更新：历史记录据此写入用户消息，TurnExecutor 据此向客户端写出用户消息与 steer-delivered。
    /// The update placed into the output stream once an external engine accepts an input: history writes the user message from it, and the TurnExecutor writes the user message and steer-delivered to the client.
    /// </summary>
    public static AgentResponseUpdate CreateDeliveredUpdate(SteerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var message = CreateModelMessage(entry);
        return new AgentResponseUpdate
        {
            MessageId = message.MessageId,
            Role = ChatRole.User,
            AuthorName = message.AuthorName,
            Contents = message.Contents,
            CreatedAt = message.CreatedAt,
            AdditionalProperties = message.AdditionalProperties,
            RawRepresentation = entry,
        };
    }

    /// <summary>
    /// 读取外部引擎已接收的输入；其他更新返回 null。
    /// Reads the input an external engine accepted; other updates return null.
    /// </summary>
    public static SteerEntry? GetDeliveredEntry(AgentResponseUpdate update) => update.RawRepresentation as SteerEntry;

    /// <summary>
    /// Steer 输入的模型消息：MessageId 等于 steerId，属性中带有 steerId。
    /// The model message of a Steer input: its MessageId equals the steerId and its properties carry the steerId.
    /// </summary>
    public static ChatMessage CreateModelMessage(SteerEntry entry)
    {
        var message = AgwMessageUtil.CreateUserChatMessage(entry.Input);
        message.MessageId = entry.SteerId.ToString("D");
        message.AdditionalProperties ??= [];
        message.AdditionalProperties[SteerIdProperty] = entry.SteerId.ToString("D");
        return message;
    }
}
