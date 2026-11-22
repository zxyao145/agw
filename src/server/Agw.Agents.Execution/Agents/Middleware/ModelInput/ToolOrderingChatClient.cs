using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware.ModelInput;

internal sealed class ToolOrderingChatClient : DelegatingChatClient
{
    public ToolOrderingChatClient(IChatClient innerClient)
        : base(innerClient) { }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(messages, Order(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(messages, Order(options), cancellationToken);

    private static ChatOptions? Order(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 1 } tools)
            return options;

        var ordered = options.Clone();
        ordered.Tools = tools.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
        return ordered;
    }
}
