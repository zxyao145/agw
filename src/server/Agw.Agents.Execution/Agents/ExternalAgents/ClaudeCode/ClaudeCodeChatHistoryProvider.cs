using Agw.Agents.Execution.Agents.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

/// <summary>
/// Delegates Claude Code response history to Agw after removing transport-only SDK data.
/// </summary>
internal sealed class ClaudeCodeChatHistoryProvider : ChatHistoryProvider
{
    private readonly ChatHistoryProvider _innerProvider;

    public ClaudeCodeChatHistoryProvider(ChatHistoryProvider innerProvider)
    {
        _innerProvider = innerProvider;
    }

    public override IReadOnlyList<string> StateKeys => _innerProvider.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    ) => _innerProvider.InvokingAsync(context, cancellationToken);

    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        if (context.InvokeException != null)
        {
#pragma warning disable MAAI001
            var failedContext = new InvokedContext(context.Agent, context.Session, [], context.InvokeException);
#pragma warning restore MAAI001
            return _innerProvider.InvokedAsync(failedContext, cancellationToken);
        }

        var responseMessages = PrepareResponseMessages(context.ResponseMessages!);
#pragma warning disable MAAI001
        var delegatedContext = new InvokedContext(context.Agent, context.Session, [], responseMessages);
#pragma warning restore MAAI001
        return _innerProvider.InvokedAsync(delegatedContext, cancellationToken);
    }

    internal static List<ChatMessage> PrepareResponseMessages(IEnumerable<ChatMessage> messages)
    {
        var responseMessages = CoalesceResponseMessages(messages)
            .Select(ExternalAgentChatHistoryAgent.CreatePersistableMessage)
            .OfType<ChatMessage>()
            .ToList();
        foreach (var responseMessage in responseMessages.Where(IsSyntheticAssistantError))
        {
            ConversationHistoryMetadata.ExcludeFromModelHistory(responseMessage);
        }
        return responseMessages;
    }

    private static IEnumerable<ChatMessage> CoalesceResponseMessages(IEnumerable<ChatMessage> messages)
    {
        // 按消息身份合并同一条消息的所有片段，保留消息首次出现的顺序及独立通知。
        // 通知或工具结果穿插在片段之间时，片段仍归属同一条消息。
        // Combine every fragment of one message by message identity, retaining first appearance
        // order and separate notifications. Notifications and tool results between two fragments
        // keep their own place while the fragments still belong to one logical message.
        List<List<ChatMessage>> groups = [];
        Dictionary<(string Id, string? Author, bool Result, string? Type), int> groupIndexes = [];
        foreach (var message in messages)
        {
            if (ClaudeCodeMessagePolicy.IsTransportEvent(message.Role, message.AdditionalProperties, message.Contents))
                continue;

            if (message.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(message.MessageId))
            {
                var key = (
                    message.MessageId,
                    message.AuthorName,
                    HasResultContent(message),
                    message.AdditionalProperties?.GetValueOrDefault("type")?.ToString()
                );
                if (groupIndexes.TryGetValue(key, out var index))
                {
                    groups[index].Add(message);
                    continue;
                }
                groupIndexes[key] = groups.Count;
            }
            groups.Add([message]);
        }
        foreach (var fragments in groups)
            yield return CompleteMessage(fragments);

        static ChatMessage CompleteMessage(List<ChatMessage> fragments) =>
            fragments.Count == 1
                ? fragments[0]
                : new ChatResponse(fragments).ToChatResponseUpdates().ToChatResponse().Messages[0];
    }

    private static bool HasResultContent(ChatMessage message) =>
        message.Contents.Any(content =>
            content.AdditionalProperties?.GetValueOrDefault("type")?.ToString() == "result"
        );

    private static bool IsSyntheticAssistantError(ChatMessage message) =>
        message.Role == ChatRole.Assistant
        && string.Equals(message.AuthorName, "<synthetic>", StringComparison.Ordinal)
        && message.Contents.Count > 0
        && message.Contents.All(content => content is ErrorContent);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _innerProvider.GetService(serviceType, serviceKey);
}
