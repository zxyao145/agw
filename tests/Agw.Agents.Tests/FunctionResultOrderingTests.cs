using System.Reflection;
using Agw.Agents.Execution.Agents.Middleware.History;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class FunctionResultOrderingTests
{
    [Fact]
    public void OrderMessages_ParallelCallsWithSharedResults_KeepsOneAtomicToolGroup()
    {
        var first = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("first", "lookup", null)])
        {
            MessageId = "first-message",
        };
        var second = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("second", "lookup", null)])
        {
            MessageId = "second-message",
        };
        var context = new ChatMessage(ChatRole.System, "node instructions");
        var results = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("first", "one"), new FunctionResultContent("second", "two")]
        );

        var ordered = FunctionResultOrderingChatClient.OrderMessages([first, second, context, results]).ToList();

        Assert.Equal([ChatRole.Assistant, ChatRole.Tool, ChatRole.System], ordered.Select(message => message.Role));
        Assert.Equal(
            ["first", "second"],
            ordered[0].Contents.OfType<FunctionCallContent>().Select(call => call.CallId)
        );
        Assert.Same(results, ordered[1]);
        Assert.Same(context, ordered[2]);
        Assert.Single(first.Contents);
        Assert.Single(second.Contents);
        var group = Assert.Single(CreateIndex(ordered).Groups, group => group.Kind == CompactionGroupKind.ToolCall);
        Assert.Equal(2, group.Messages.Count);
        Assert.Equal(ordered, FunctionResultOrderingChatClient.OrderMessages(ordered));
    }

    [Fact]
    public void OrderMessages_SeparateToolRounds_PreservesRoundBoundaries()
    {
        var firstCall = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("first", "lookup", null)]);
        var firstResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("first", "one")]);
        var secondCall = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("second", "lookup", null)]);
        var secondResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("second", "two")]);
        ChatMessage[] messages = [firstCall, firstResult, secondCall, secondResult];

        var ordered = FunctionResultOrderingChatClient.OrderMessages(messages).ToList();

        Assert.Equal(messages, ordered);
        Assert.Equal(2, CreateIndex(ordered).Groups.Count);
    }

    private static CompactionMessageIndex CreateIndex(IList<ChatMessage> messages) =>
        (CompactionMessageIndex)
            typeof(CompactionMessageIndex)
                .GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [messages, null])!;
}
