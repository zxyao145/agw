using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Keeps tool results adjacent to function calls loaded by per-service chat history persistence.
/// </summary>
internal sealed class FunctionResultOrderingChatClient : DelegatingChatClient
{
    public FunctionResultOrderingChatClient(IChatClient innerClient)
        : base(innerClient) { }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetResponseAsync(OrderMessages(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => base.GetStreamingResponseAsync(OrderMessages(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> OrderMessages(IEnumerable<ChatMessage> messages)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var contextCount = messageList
            .TakeWhile(message =>
                message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.AIContextProvider
            )
            .Count();
        var resultCount = messageList
            .Skip(contextCount)
            .TakeWhile(message =>
                message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any()
            )
            .Count();
        if (contextCount > 0 && resultCount > 0)
        {
            // Before history is loaded, only injected context may precede the in-flight results.
            messageList = messageList
                .Skip(contextCount)
                .Take(resultCount)
                .Concat(messageList.Take(contextCount))
                .Concat(messageList.Skip(contextCount + resultCount))
                .ToList();
        }
        var ordered = new List<ChatMessage>(messageList.Count);
        for (var index = 0; index < messageList.Count; index++)
        {
            var message = messageList[index];
            ordered.Add(message);
            if (message.Role != ChatRole.Assistant)
                continue;
            var callIds = message
                .Contents.OfType<FunctionCallContent>()
                .Select(call => call.CallId)
                .ToHashSet(StringComparer.Ordinal);
            if (callIds.Count == 0)
                continue;

            var deferred = new List<ChatMessage>();
            // Work on the merged history/request, stopping at the next assistant response.
            // A result may cross intervening user/context messages only when its call is known.
            while (
                index + 1 < messageList.Count
                && (
                    messageList[index + 1].Role != ChatRole.Assistant
                    || messageList[index + 1].GetAgentRequestMessageSourceType()
                        == AgentRequestMessageSourceType.AIContextProvider
                )
            )
            {
                var next = messageList[++index];
                var results = next.Contents.OfType<FunctionResultContent>().ToList();
                if (
                    next.Role == ChatRole.Tool
                    && results.Count > 0
                    && results.All(result => callIds.Contains(result.CallId))
                )
                    ordered.Add(next);
                else
                    deferred.Add(next);
            }
            ordered.AddRange(deferred);
        }
        return ordered;
    }
}
