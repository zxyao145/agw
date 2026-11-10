using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Context;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Steering;

/// <summary>
/// 每次模型调用前取走 Turn 的 SteerInbox，把输入作为用户消息追加到本次请求末尾，位于上一 Step 的工具结果之后。
/// 它位于历史持久化层之外，所以追加的消息与工具结果一起被写入历史，下一次读取历史得到的顺序与模型看到的顺序一致。
/// Takes the turn's SteerInbox before every model call and appends its inputs as user messages at the end of the request, after the previous Step's tool results.
/// It sits outside the history persistence layer, so the appended messages are written to history together with the tool results and the next history read matches the order the model saw.
/// </summary>
internal sealed class SteerInjectionChatClient : DelegatingChatClient
{
    public SteerInjectionChatClient(IChatClient innerClient)
        : base(innerClient) { }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => await base.GetResponseAsync(await InjectAsync(messages, cancellationToken), options, cancellationToken);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var injected = await InjectAsync(messages, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(injected, options, cancellationToken))
        {
            yield return update;
        }
    }

    private static async Task<IEnumerable<ChatMessage>> InjectAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        var scope = ExecutionScope.Current;
        if (scope?.Steers is not { } inbox)
            return messages;
        var taken = await inbox.TakePendingAsync(cancellationToken);
        if (taken.Count == 0)
            return messages;
        var delivered = await SteerDelivery.DeliverAsync(scope, taken, cancellationToken);
        return [.. messages, .. delivered];
    }
}
