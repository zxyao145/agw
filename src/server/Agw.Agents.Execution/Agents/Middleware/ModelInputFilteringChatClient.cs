using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Removes transient content that model-provider adapters cannot encode as input.
/// </summary>
internal sealed class ModelInputFilteringChatClient : DelegatingChatClient
{
    public ModelInputFilteringChatClient(IChatClient innerClient)
        : base(innerClient) { }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(FilterMessages(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(FilterMessages(messages), options, cancellationToken);

    private static List<ChatMessage> FilterMessages(IEnumerable<ChatMessage> messages)
    {
        var filteredMessages = new List<ChatMessage>();
        foreach (var message in messages)
        {
            var contents = message.Contents.Where(IsModelInputContent).ToList();
            if (contents.Count == 0)
            {
                continue;
            }

            if (contents.Count == message.Contents.Count)
            {
                filteredMessages.Add(message);
                continue;
            }

            var filteredMessage = message.Clone();
            filteredMessage.Contents = contents;
            filteredMessages.Add(filteredMessage);
        }

        return filteredMessages;
    }

    private static bool IsModelInputContent(AIContent content) =>
        content switch
        {
            TextContent text => !string.IsNullOrEmpty(text.Text),
            // Function approvals are consumed by FunctionInvokingChatClient; MCP approvals remain provider input.
            ToolApprovalResponseContent { ToolCall: FunctionCallContent } => false,
            _ => true,
        };
}
