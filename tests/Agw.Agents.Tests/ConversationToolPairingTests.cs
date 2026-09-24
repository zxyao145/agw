using Agw.Agents.Execution.Agents.History;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ConversationToolPairingTests
{
    [Fact]
    public void Filter_ReusedCallId_PreservesOnlyAnsweredOccurrence()
    {
        var first = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")]);
        var answer = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "first result")]);
        var pending = new ChatMessage(
            ChatRole.Assistant,
            [new TextContent("partial"), new FunctionCallContent("call", "lookup")]
        );

        var result = Filter([first, answer, new(ChatRole.User, "next question"), pending]);

        Assert.Single(result.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Same(first, result[0]);
        Assert.Same(answer, result[1]);
        Assert.Equal("partial", result[^1].Text);
    }

    [Fact]
    public void Filter_ResultInFollowingRequest_PreservesMatchingHistoricalCall()
    {
        var call = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")]);
        var result = Filter(
            [call, new(ChatRole.Assistant, "working")],
            [new(ChatRole.Tool, [new FunctionResultContent("call", "answer")])]
        );

        Assert.Equal(2, result.Count);
        Assert.Same(call, result[0]);
    }

    [Fact]
    public void Filter_ResultBeforeCall_PreservesTextAndRemovesUnpairedContents()
    {
        var result = Filter([
            new(ChatRole.Tool, [new FunctionResultContent("call", "old result")]),
            new(ChatRole.Assistant, [new TextContent("partial"), new FunctionCallContent("call", "lookup")]),
        ]);

        Assert.Equal("partial", Assert.Single(result).Text);
        Assert.IsType<TextContent>(Assert.Single(result[0].Contents));
    }

    [Fact]
    public void Filter_ReusedCallIdWithTwoAnswers_PreservesBothPairs()
    {
        var result = Filter([
            new(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "first")]),
            new(ChatRole.Assistant, [new FunctionCallContent("call", "lookup")]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "second")]),
            new(ChatRole.Tool, [new FunctionResultContent("call", "duplicate")]),
        ]);

        Assert.Equal(4, result.Count);
        Assert.Equal(
            ["first", "second"],
            result
                .SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .Select(content => content.Result)
        );
    }

    private static IReadOnlyList<ChatMessage> Filter(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatMessage>? followingMessages = null
    ) => AgwChatHistoryProvider.RemoveIncompleteFunctionCallsAndOrphanedResults(messages, followingMessages ?? []);
}
