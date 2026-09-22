using System.Linq;
using System.Threading.Tasks;
using Agw.Agents.Execution.Agents.History;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001

namespace Agw.Agents.Tests;

public sealed class ToolResultEvictionCompactionStrategyTests
{
    private const int ResultLength = 20_000;

    [Fact]
    public async Task CompactAsync_OverBudget_EvictsOldestResultsUntilTargetAndKeepsAssistantOutput()
    {
        // Arrange: three tool-call groups of roughly 5,000 tokens each; the target is met after one eviction.
        var messages = CreateConversation(groups: 3);
        var strategy = new ToolResultEvictionCompactionStrategy(
            CompactionTriggers.TokensExceed(12_000),
            minimumPreservedGroups: 1
        );

        // Act
        var compacted = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        // Assert: the oldest group loses only its result body; every other message is the original instance.
        Assert.Equal(messages.Count, compacted.Count);
        Assert.Same(messages[0], compacted[0]);
        Assert.Same(messages[1], compacted[1]);
        var evictedResult = Assert.Single(compacted[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", evictedResult.CallId);
        Assert.Equal(ToolResultEvictionCompactionStrategy.EvictedResultText, evictedResult.Result);
        Assert.Equal(messages[2].MessageId, compacted[2].MessageId);
        Assert.Equal(
            ResultLength,
            Assert.Single(messages[2].Contents.OfType<FunctionResultContent>()).Result!.ToString()!.Length
        );
        Assert.Equal(messages.Skip(3), compacted.Skip(3), ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public async Task CompactAsync_PreservedGroups_AreNeverEvicted()
    {
        // Arrange: every group is within the preserved window even though the trigger fires.
        var messages = CreateConversation(groups: 2);
        var strategy = new ToolResultEvictionCompactionStrategy(
            CompactionTriggers.TokensExceed(100),
            minimumPreservedGroups: 3
        );

        // Act
        var compacted = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        // Assert
        Assert.Equal(messages, compacted, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public async Task CompactAsync_UnderBudget_LeavesMessagesUnchanged()
    {
        // Arrange
        var messages = CreateConversation(groups: 3);
        var strategy = new ToolResultEvictionCompactionStrategy(
            CompactionTriggers.TokensExceed(1_000_000),
            minimumPreservedGroups: 1
        );

        // Act
        var compacted = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        // Assert
        Assert.Equal(messages, compacted, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public async Task Create_BetweenEvictionAndTruncationThresholds_EvictsResultsWithoutDroppingMessages()
    {
        // Arrange: budget 40,000 tokens; four groups (~20,000 tokens) exceed the 50% eviction threshold
        // but stay below the 80% truncation threshold.
        var messages = CreateConversation(groups: 4);
        var strategy = ContextWindowCompactionPipeline.Create(maxContextWindowTokens: 40_000, maxOutputTokens: 0);

        // Act
        var compacted = (await CompactionProvider.CompactAsync(strategy, messages)).ToList();

        // Assert
        Assert.Equal(messages.Count, compacted.Count);
        var evicted = compacted
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => Equals(result.Result, ToolResultEvictionCompactionStrategy.EvictedResultText))
            .ToList();
        Assert.Equal("call-1", Assert.Single(evicted).CallId);
        Assert.Equal(4, compacted.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).Count());
        Assert.Equal(4, compacted.SelectMany(message => message.Contents.OfType<TextReasoningContent>()).Count());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1_000, -1)]
    [InlineData(1_000, 1_000)]
    public void Create_InvalidLimits_Throws(int maxContextWindowTokens, int maxOutputTokens)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ContextWindowCompactionPipeline.Create(maxContextWindowTokens, maxOutputTokens)
        );
    }

    [Fact]
    public async Task Create_DeepSeekResponsesRequest_KeepsReasoningBeforeEvictedToolCall()
    {
        // Arrange: DeepSeek thinking mode rejects a current round whose first assistant output lacks reasoning_text.
        var messages = CreateConversation(groups: 4);
        var compacted = (
            await CompactionProvider.CompactAsync(
                ContextWindowCompactionPipeline.Create(maxContextWindowTokens: 40_000, maxOutputTokens: 0),
                messages
            )
        ).ToList();
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture("https://api.deepseek.com");

        // Act
        await fixture.Client.GetResponseAsync(
            compacted,
            new ChatOptions { Tools = [AIFunctionFactory.Create(() => "result", "lookup")] },
            TestContext.Current.CancellationToken
        );

        // Assert: the request replays reasoning, call and evicted output of the oldest group in order.
        var inputs = Assert.Single(fixture.Handler.Requests).GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("message", inputs[0].GetProperty("type").GetString());
        Assert.Equal("reasoning", inputs[1].GetProperty("type").GetString());
        Assert.Equal("think-1", inputs[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("function_call", inputs[2].GetProperty("type").GetString());
        Assert.Equal("call-1", inputs[2].GetProperty("call_id").GetString());
        Assert.Equal("function_call_output", inputs[3].GetProperty("type").GetString());
        Assert.Equal("call-1", inputs[3].GetProperty("call_id").GetString());
        Assert.Equal(
            ToolResultEvictionCompactionStrategy.EvictedResultText,
            inputs[3].GetProperty("output").GetString()
        );
        Assert.DoesNotContain(
            inputs,
            item =>
                item.GetProperty("type").GetString() == "message" && item.GetProperty("role").GetString() == "assistant"
        );
    }

    private static List<ChatMessage> CreateConversation(int groups)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "question") };
        for (var index = 1; index <= groups; index++)
        {
            messages.Add(
                new ChatMessage(
                    ChatRole.Assistant,
                    [new TextReasoningContent($"think-{index}"), new FunctionCallContent($"call-{index}", "lookup")]
                )
                {
                    MessageId = $"assistant-{index}",
                }
            );
            messages.Add(
                new ChatMessage(
                    ChatRole.Tool,
                    [new FunctionResultContent($"call-{index}", new string('x', ResultLength))]
                )
                {
                    MessageId = $"tool-{index}",
                }
            );
        }

        return messages;
    }
}
