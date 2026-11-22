using Agw.Shared.Tooling;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

#pragma warning disable MAAI001 // MAF compaction APIs are marked experimental.

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// <para>按模型上下文窗口构建 Definition Agent 的两阶段压缩策略。</para>
/// <para>Builds the two-stage compaction strategy of Definition Agents from the model context window.</para>
/// </summary>
/// <remarks>
/// <para>Tool result 清空与消息截断都保留最近两个非 System 消息组。</para>
/// <para>Tool result eviction and message truncation each preserve the two most recent non-System groups.</para>
/// </remarks>
internal static class ContextWindowCompactionPipeline
{
    public const double ToolEvictionThreshold = 0.5;
    public const double ToolEvictionTargetRatio = 0.6;
    public const double TruncationThreshold = 0.8;
    public const double TruncationTargetRatio = 0.8;
    public const int MinimumPreservedGroups = 2;
    private const int MaxToolEvictionThresholdTokens = 64_000;

    /// <summary>
    /// <para>创建 Tool result 清空与消息截断组成的压缩 pipeline。</para>
    /// <para>Creates the compaction pipeline of tool-result eviction followed by message truncation.</para>
    /// </summary>
    /// <param name="maxContextWindowTokens">
    /// <para>模型上下文窗口的 token 上限，必须为正数。</para>
    /// <para>Model context-window token limit; must be positive.</para>
    /// </param>
    /// <param name="maxOutputTokens">
    /// <para>模型输出 token 上限，必须小于上下文窗口。</para>
    /// <para>Model output token limit; must be smaller than the context window.</para>
    /// </param>
    /// <returns>
    /// <para>两阶段压缩策略。</para>
    /// <para>Two-stage compaction strategy.</para>
    /// </returns>
    public static CompactionStrategy Create(int maxContextWindowTokens, int maxOutputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContextWindowTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputTokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(maxOutputTokens, maxContextWindowTokens);

        var inputBudgetTokens = maxContextWindowTokens - maxOutputTokens;
        var toolEvictionTokens = Math.Min(
            (int)(inputBudgetTokens * ToolEvictionThreshold),
            MaxToolEvictionThresholdTokens
        );
        return new PipelineCompactionStrategy(
            new ToolResultEvictionCompactionStrategy(
                CompactionTriggers.TokensExceed(toolEvictionTokens),
                MinimumPreservedGroups,
                CompactionTriggers.TokensBelow((int)(toolEvictionTokens * ToolEvictionTargetRatio) + 1)
            ),
            new HistoryTruncationCompactionStrategy(
                CompactionTriggers.TokensExceed((int)(inputBudgetTokens * TruncationThreshold)),
                CompactionTriggers.TokensBelow((int)(toolEvictionTokens * TruncationTargetRatio) + 1)
            )
        );
    }

    internal static IReadOnlyList<CompactionMessageGroup> GetOlderGroups(
        CompactionMessageIndex index,
        int minimumPreservedGroups
    ) =>
        index
            .Groups.Where(group => !group.IsExcluded && group.Kind != CompactionGroupKind.System)
            .SkipLast(minimumPreservedGroups)
            .ToArray();

    internal static HashSet<string> GetAskUserQuestionCallIds(CompactionMessageIndex index) =>
        index
            .GetIncludedMessages()
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Where(call => call.Name == ToolDefinitionNames.AskUserQuestion)
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);

    private sealed class HistoryTruncationCompactionStrategy : CompactionStrategy
    {
        public HistoryTruncationCompactionStrategy(CompactionTrigger trigger, CompactionTrigger target)
            : base(trigger, target) { }

        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index,
            ILogger logger,
            CancellationToken cancellationToken
        )
        {
            var askUserQuestionCallIds = GetAskUserQuestionCallIds(index);
            var candidates = GetOlderGroups(index, MinimumPreservedGroups);
            var compacted = false;
            foreach (var group in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var containsUserQuestion = group
                    .Messages.SelectMany(message => message.Contents)
                    .Any(content =>
                        (content is FunctionCallContent call && askUserQuestionCallIds.Contains(call.CallId))
                        || (content is FunctionResultContent result && askUserQuestionCallIds.Contains(result.CallId))
                    );
                if (containsUserQuestion)
                    continue;

                group.IsExcluded = true;
                group.ExcludeReason = "Older message group truncated";
                compacted = true;
                if (Target(index))
                    break;
            }

            return ValueTask.FromResult(compacted);
        }
    }
}
