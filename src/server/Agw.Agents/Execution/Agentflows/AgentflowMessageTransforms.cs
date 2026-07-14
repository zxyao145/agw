using Microsoft.Extensions.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

internal static class AgentflowMessageTransforms
{
    /// <summary>
    /// 将工作流上游 Agent 的输出转换为下游 Agent 可安全消费的输入。
    /// </summary>
    internal static List<ChatMessage> CreatePortableAgentInput(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlySet<string> pendingFunctionCallIds)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Assistant && message.Role != ChatRole.Tool)
            {
                result.Add(message);
                continue;
            }

            // 外部工具结果会回到同一个 workflow executor；只允许当前会话正在等待的调用继续保留协议角色。
            if (message.Role == ChatRole.Tool)
            {
                var continuationContents = message.Contents
                    .Where(content =>
                        content is not FunctionResultContent functionResult ||
                        pendingFunctionCallIds.Contains(functionResult.CallId))
                    .ToList();
                if (continuationContents.OfType<FunctionResultContent>().Any())
                {
                    if (continuationContents.Count == message.Contents.Count)
                    {
                        result.Add(message);
                    }
                    else
                    {
                        var continuationMessage = message.Clone();
                        continuationMessage.Contents = continuationContents;
                        result.Add(continuationMessage);
                    }

                    continue;
                }
            }

            var contents = message.Contents
                .Where(content => content is TextContent or DataContent or UriContent)
                .ToList();
            if (contents.Count == 0)
            {
                continue;
            }

            var portableMessage = message.Clone();
            portableMessage.Role = ChatRole.User;
            portableMessage.Contents = contents;
            result.Add(portableMessage);
        }

        return result;
    }

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
