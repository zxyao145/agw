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
        var lastFunctionResultIndex = -1;
        for (var index = messageList.Count - 1; index >= 0; index--)
        {
            if (IsFunctionResultMessage(messageList[index]))
            {
                lastFunctionResultIndex = index;
                break;
            }
        }

        if (lastFunctionResultIndex <= 0)
        {
            return messageList;
        }

        var functionResultStart = lastFunctionResultIndex;
        while (functionResultStart > 0 && IsFunctionResultMessage(messageList[functionResultStart - 1]))
        {
            functionResultStart--;
        }

        var functionResultEnd = lastFunctionResultIndex + 1;
        while (functionResultEnd < messageList.Count && IsFunctionResultMessage(messageList[functionResultEnd]))
        {
            functionResultEnd++;
        }

        var resultCallIds = messageList
            .Skip(functionResultStart)
            .Take(functionResultEnd - functionResultStart)
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(content => content.CallId)
            .ToHashSet(StringComparer.Ordinal);
        var functionCallIndex = -1;
        HashSet<string>? functionCallIds = null;
        for (var index = functionResultStart - 1; index >= 0; index--)
        {
            if (messageList[index].Role != ChatRole.Assistant)
            {
                continue;
            }

            var candidateCallIds = messageList[index]
                .Contents.OfType<FunctionCallContent>()
                .Select(content => content.CallId)
                .ToHashSet(StringComparer.Ordinal);
            if (resultCallIds.IsSubsetOf(candidateCallIds))
            {
                functionCallIndex = index;
                functionCallIds = candidateCallIds;
                break;
            }
        }

        var insertionIndex = 0;
        if (functionCallIndex >= 0 && functionCallIds != null)
        {
            insertionIndex = functionCallIndex + 1;
            while (
                insertionIndex < functionResultStart
                && IsFunctionResultMessageForCalls(messageList[insertionIndex], functionCallIds)
            )
            {
                insertionIndex++;
            }

            if (insertionIndex == functionResultStart)
            {
                return messageList;
            }
        }

        for (var index = insertionIndex; index < functionResultStart; index++)
        {
            if (
                messageList[index].GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider
            )
            {
                return messageList;
            }
        }

        // Per-service persistence prepends the matching function call, while context providers can
        // leave their messages before the in-flight result. Move only that final result group.
        var reordered = messageList.ToList();
        var functionResults = reordered.GetRange(functionResultStart, functionResultEnd - functionResultStart);
        reordered.RemoveRange(functionResultStart, functionResults.Count);
        reordered.InsertRange(insertionIndex, functionResults);
        return reordered;
    }

    private static bool IsFunctionResultMessage(ChatMessage message) =>
        message.Role == ChatRole.Tool && message.Contents.OfType<FunctionResultContent>().Any();

    private static bool IsFunctionResultMessageForCalls(ChatMessage message, IReadOnlySet<string> functionCallIds)
    {
        if (message.Role != ChatRole.Tool)
        {
            return false;
        }

        var functionResults = message.Contents.OfType<FunctionResultContent>().ToList();
        return functionResults.Count > 0 && functionResults.All(result => functionCallIds.Contains(result.CallId));
    }
}
