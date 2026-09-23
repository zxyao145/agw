using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAI001 // MAF compaction APIs are marked experimental.

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// <para>清空较旧 Tool call 组中的结果正文，原样保留 Assistant 消息里的推理和调用。</para>
/// <para>Evicts result bodies from older tool-call groups while keeping the reasoning and calls in assistant messages intact.</para>
/// </summary>
/// <remarks>
/// <para>模型服务按各自协议校验回放的 Assistant 输出：DeepSeek thinking 模式要求本轮 Assistant 输出附带 reasoning_text，OpenAI Responses 要求推理条目后紧跟原始条目。只改写 Tool result 正文时，请求在所有协议下都保持合法，调用与结果的配对也不变。</para>
/// <para>Model services validate replayed assistant output by protocol: DeepSeek thinking mode requires reasoning_text alongside the current round's assistant output, and OpenAI Responses requires reasoning items to be followed by their original items. Rewriting only tool result bodies keeps requests valid under every protocol and preserves call/result pairing.</para>
/// </remarks>
internal sealed class ToolResultEvictionCompactionStrategy : CompactionStrategy
{
    /// <summary>
    /// <para>替换已清空 Tool result 正文的占位文本。</para>
    /// <para>Placeholder text replacing an evicted tool result body.</para>
    /// </summary>
    public const string EvictedResultText = "[Tool result removed from context by compaction]";

    private readonly int _minimumPreservedGroups;

    /// <summary>
    /// <para>创建 ToolResultEvictionCompactionStrategy 实例。</para>
    /// <para>Initializes ToolResultEvictionCompactionStrategy.</para>
    /// </summary>
    /// <param name="trigger">
    /// <para>触发压缩的条件。</para>
    /// <para>Condition that triggers compaction.</para>
    /// </param>
    /// <param name="minimumPreservedGroups">
    /// <para>始终保留、不清空结果的最近非 System 消息组数量。</para>
    /// <para>Number of most recent non-system message groups whose results are never evicted.</para>
    /// </param>
    /// <param name="target">
    /// <para>压缩停止条件；为空时使用触发条件的否定。</para>
    /// <para>Condition at which compaction stops; null negates the trigger.</para>
    /// </param>
    public ToolResultEvictionCompactionStrategy(
        CompactionTrigger trigger,
        int minimumPreservedGroups,
        CompactionTrigger? target = null
    )
        : base(trigger, target)
    {
        _minimumPreservedGroups = EnsureNonNegative(minimumPreservedGroups);
    }

    /// <summary>
    /// <para>由旧到新清空候选 Tool call 组的结果正文，达到目标后停止。</para>
    /// <para>Evicts result bodies of candidate tool-call groups from oldest to newest, stopping once the target is met.</para>
    /// </summary>
    /// <param name="index">
    /// <para>当前请求的消息组索引。</para>
    /// <para>Message-group index of the current request.</para>
    /// </param>
    /// <param name="logger">
    /// <para>压缩日志记录器。</para>
    /// <para>Compaction logger.</para>
    /// </param>
    /// <param name="cancellationToken">
    /// <para>用于取消当前异步操作的令牌。</para>
    /// <para>Token used to cancel the current asynchronous operation.</para>
    /// </param>
    /// <returns>
    /// <para>发生了任何清空时为 true。</para>
    /// <para>True when any result was evicted.</para>
    /// </returns>
    protected override ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var activeGroups = index
            .Groups.Where(group => !group.IsExcluded && group.Kind != CompactionGroupKind.System)
            .ToList();
        var preservedGroups = activeGroups
            .Skip(Math.Max(0, activeGroups.Count - _minimumPreservedGroups))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var candidates = index
            .Groups.Where(group =>
                !group.IsExcluded
                && group.Kind == CompactionGroupKind.ToolCall
                && !preservedGroups.Contains(group)
                && group.Messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any())
            )
            .ToList();

        var compacted = false;
        foreach (var group in candidates)
        {
            var position = index.Groups.IndexOf(group);
            group.IsExcluded = true;
            group.ExcludeReason = "Tool results evicted by ToolResultEvictionCompactionStrategy";
            // 替换组以 Summary 类型插入：不计入原始消息数，后续请求也不会再次成为候选。
            // The replacement group is inserted as Summary: it is excluded from the raw message count and never becomes a candidate again.
            index.InsertGroup(
                position + 1,
                CompactionGroupKind.Summary,
                group.Messages.Select(EvictResults).ToList(),
                group.TurnIndex
            );
            compacted = true;
            if (Target(index))
            {
                break;
            }
        }

        return new ValueTask<bool>(compacted);
    }

    /// <summary>
    /// <para>把消息中的每个 Tool result 正文替换为占位文本，其他消息原样返回。</para>
    /// <para>Replaces every tool result body in the message with the placeholder text, returning other messages unchanged.</para>
    /// </summary>
    /// <param name="message">
    /// <para>待处理的消息。</para>
    /// <para>Message to process.</para>
    /// </param>
    /// <returns>
    /// <para>结果正文已替换的消息副本，或无 Tool result 时的原消息。</para>
    /// <para>Message copy with replaced result bodies, or the original message when it has no tool result.</para>
    /// </returns>
    private static ChatMessage EvictResults(ChatMessage message)
    {
        if (!message.Contents.OfType<FunctionResultContent>().Any())
        {
            return message;
        }

        var copy = message.Clone();
        copy.Contents = message
            .Contents.Select(content =>
                content is FunctionResultContent result
                    ? new FunctionResultContent(result.CallId, EvictedResultText)
                    : content
            )
            .ToList();
        return copy;
    }
}
