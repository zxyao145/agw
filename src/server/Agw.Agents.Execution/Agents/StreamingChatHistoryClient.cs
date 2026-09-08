using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents;

/// <summary>Runs inside MAF's per-call history loader, so inputs are captured after history is read.</summary>
internal sealed class StreamingChatHistoryClient : DelegatingChatClient
{
    private readonly IStreamingConversationHistoryProvider _history;

    public StreamingChatHistoryClient(IChatClient innerClient, IStreamingConversationHistoryProvider history)
        : base(innerClient)
    {
        _history = history;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var context = AIAgent.CurrentRunContext;
        var input = messages.ToList();
        var history =
            context?.Session == null
                ? null
                : await _history
                    .BeginStreamingResponseAsync(context.Agent, context.Session, input, cancellationToken)
                    .ConfigureAwait(false);
        await foreach (
            var update in base.GetStreamingResponseAsync(input, options, cancellationToken).ConfigureAwait(false)
        )
        {
            if (history != null)
                await history.AppendAsync(update, cancellationToken).ConfigureAwait(false);
            yield return update;
        }
    }
}
