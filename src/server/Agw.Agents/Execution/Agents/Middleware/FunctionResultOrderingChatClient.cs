using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

/// <summary>
/// Keeps tool results adjacent to function calls loaded by per-service chat history persistence.
/// </summary>
internal sealed class FunctionResultOrderingChatClient : DelegatingChatClient
{
    public FunctionResultOrderingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(OrderMessages(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(OrderMessages(messages), options, cancellationToken);

    private static IEnumerable<ChatMessage> OrderMessages(IEnumerable<ChatMessage> messages)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var firstFunctionResultIndex = -1;
        for (var index = 0; index < messageList.Count; index++)
        {
            if (IsFunctionResultMessage(messageList[index]))
            {
                firstFunctionResultIndex = index;
                break;
            }
        }

        if (firstFunctionResultIndex <= 0)
        {
            return messageList;
        }

        // Approval continuations retain context-provider messages and append the tool result after them.
        // Per-service persistence loads the matching function call later, so the result must lead this request.
        for (var index = 0; index < firstFunctionResultIndex; index++)
        {
            if (messageList[index].GetAgentRequestMessageSourceType() !=
                AgentRequestMessageSourceType.AIContextProvider)
            {
                return messageList;
            }
        }

        var functionResultEnd = firstFunctionResultIndex + 1;
        while (functionResultEnd < messageList.Count &&
               IsFunctionResultMessage(messageList[functionResultEnd]))
        {
            functionResultEnd++;
        }

        var reordered = new List<ChatMessage>(messageList.Count);
        for (var index = firstFunctionResultIndex; index < functionResultEnd; index++)
        {
            reordered.Add(messageList[index]);
        }

        for (var index = 0; index < firstFunctionResultIndex; index++)
        {
            reordered.Add(messageList[index]);
        }

        for (var index = functionResultEnd; index < messageList.Count; index++)
        {
            reordered.Add(messageList[index]);
        }

        return reordered;
    }

    private static bool IsFunctionResultMessage(ChatMessage message) =>
        message.Role == ChatRole.Tool &&
        message.Contents.OfType<FunctionResultContent>().Any();
}
