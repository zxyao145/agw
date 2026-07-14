using Microsoft.Extensions.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

internal static class AgentflowMessageTransforms
{
    /// <summary>
    /// 将节点指令作为平台生成的 system 消息添加到现有消息列表之前。
    /// </summary>
    internal static List<ChatMessage> ApplyInstructions(
        IReadOnlyList<ChatMessage> messages,
        string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return messages.ToList();
        }

        var result = new List<ChatMessage>
        {
            new(ChatRole.System, instructions)
            {
                AuthorName = Constants.DefaultInputAuthor,
            },
        };
        result.AddRange(messages);
        return result;
    }

    /// <summary>
    /// 将其他 Agent 的可移植 assistant 消息重标记为 user，使目标 Agent 将上游结果作为输入处理。
    /// 当前 Agent 自己的消息，以及包含工具调用等不可移植内容的消息保持不变。
    /// </summary>
    internal static List<ChatMessage> ReassignOtherAgentsAsUsers(
        IReadOnlyList<ChatMessage> messages,
        string targetAgentName)
    {
        return messages
            .Select(message =>
            {
                if (message.Role != ChatRole.Assistant
                    || string.Equals(message.AuthorName, targetAgentName, StringComparison.Ordinal)
                    || message.Contents.Any(content =>
                        content is not TextContent
                            and not DataContent
                            and not UriContent
                            and not UsageContent))
                {
                    return message;
                }

                var reassignedMessage = message.Clone();
                reassignedMessage.Role = ChatRole.User;
                return reassignedMessage;
            })
            .ToList();
    }
}
