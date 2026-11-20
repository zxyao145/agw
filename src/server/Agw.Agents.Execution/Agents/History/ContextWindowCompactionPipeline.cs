using Microsoft.Agents.AI.Compaction;

#pragma warning disable MAAI001 // MAF compaction APIs are marked experimental.

namespace Agw.Agents.Execution.Agents.History;

/// <summary>
/// <para>按模型上下文窗口构建 Definition Agent 的两阶段压缩策略。</para>
/// <para>Builds the two-stage compaction strategy of Definition Agents from the model context window.</para>
/// </summary>
/// <remarks>
/// <para>输入预算为上下文窗口减去输出上限。旧工具结果的清理阈值为输入预算的 50%，最高 64,000 Token。
/// 消息截断阈值为输入预算的 80%。两个阶段都保留最近两个非 System 消息组。</para>
/// <para>The input budget is the context window minus the output limit. Tool results are evicted above half that budget
/// or 64,000 tokens, whichever is lower. Message truncation starts above 80%. Both stages retain two non-system groups.</para>
/// </remarks>
internal static class ContextWindowCompactionPipeline
{
    public const double ToolEvictionThreshold = 0.5;
    public const double TruncationThreshold = 0.8;
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
                MinimumPreservedGroups
            ),
            new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed((int)(inputBudgetTokens * TruncationThreshold)),
                MinimumPreservedGroups
            )
        );
    }
}
