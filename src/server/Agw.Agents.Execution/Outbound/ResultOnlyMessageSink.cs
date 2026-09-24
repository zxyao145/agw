namespace Agw.Agents.Execution.Outbound;

/// <summary>
/// <para>只把 result 消息、轮次控制消息和错误消息写入内层 sink，丢弃本轮的过程消息。</para>
/// <para>Writes only result messages, turn control messages, and errors to the inner sink, dropping the turn's process messages.</para>
/// </summary>
/// <remarks>
/// <para>对话历史由 runtime 独立写入，不经过 sink，所以过滤只影响实时推送。</para>
/// <para>Conversation history is written by the runtime independently of the sink, so filtering affects live streaming only.</para>
/// </remarks>
internal sealed class ResultOnlyMessageSink : IExecutionMessageSink
{
    private readonly IExecutionMessageSink _inner;

    public ResultOnlyMessageSink(IExecutionMessageSink inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>
    /// <para>启用过滤时包装 sink，否则原样返回。</para>
    /// <para>Wraps the sink when filtering is enabled, otherwise returns it unchanged.</para>
    /// </summary>
    public static IExecutionMessageSink Wrap(IExecutionMessageSink inner, bool resultOnly) =>
        resultOnly ? new ResultOnlyMessageSink(inner) : inner;

    public ValueTask WriteAsync(AgwMessage message, CancellationToken cancellationToken) =>
        ShouldWrite(message) ? _inner.WriteAsync(message, cancellationToken) : ValueTask.CompletedTask;

    internal static bool ShouldWrite(AgwMessage message) =>
        AgwMessageClassifier.IsResult(message) || AgwMessageClassifier.IsControl(message) || HasError(message);

    private static bool HasError(AgwMessage message) => message.Contents.OfType<AgwErrorContent>().Any();
}
