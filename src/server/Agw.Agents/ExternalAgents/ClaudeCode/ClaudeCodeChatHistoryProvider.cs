using Agw.Agents.Execution.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.ExternalAgents.ClaudeCode;

/// <summary>
/// Delegates Claude Code history to Agw after removing transport-only data from completed SDK messages.
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
            return _innerProvider.InvokedAsync(context, cancellationToken);
        }

        var responseMessages = context
            .ResponseMessages!.Select(ExternalAgentChatHistoryAgent.CreatePersistableMessage)
            .OfType<ChatMessage>()
            .ToList();
        foreach (var responseMessage in responseMessages.Where(IsSyntheticAssistantError))
        {
            ConversationHistoryMetadata.ExcludeFromModelHistory(responseMessage);
        }
#pragma warning disable MAAI001
        var delegatedContext = new InvokedContext(
            context.Agent,
            context.Session,
            context.RequestMessages,
            responseMessages
        );
#pragma warning restore MAAI001
        return _innerProvider.InvokedAsync(delegatedContext, cancellationToken);
    }

    private static bool IsSyntheticAssistantError(ChatMessage message) =>
        message.Role == ChatRole.Assistant
        && string.Equals(message.AuthorName, "<synthetic>", StringComparison.Ordinal)
        && message.Contents.Count > 0
        && message.Contents.All(content => content is ErrorContent);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _innerProvider.GetService(serviceType, serviceKey);
}
