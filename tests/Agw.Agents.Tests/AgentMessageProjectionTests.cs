using System.Text.Json;
using Agw.Agents.Execution.Agents.History;
using Agw.Projects.Contracts.History;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class AgentMessageProjectionTests
{
    [Fact]
    public void ModelDeltas_OmittedRoleAndAuthor_KeepMessageAndBlockBoundaries()
    {
        var projection = CreateProjection();
        var adapter = new ModelMessageAdapter();
        var updates = new[]
        {
            new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("reason")])
            {
                MessageId = "answer",
                AuthorName = "model",
            },
            new AgentResponseUpdate { MessageId = "answer", Contents = [new TextContent("a")] },
            new AgentResponseUpdate { MessageId = "answer", Contents = [new FunctionCallContent("call", "lookup")] },
            new AgentResponseUpdate { MessageId = "answer", Contents = [new TextContent("b")] },
        };
        foreach (var update in updates)
        foreach (var operation in adapter.Map(update))
            projection.Apply(operation);

        var message = Deserialize(Assert.Single(projection.CapturePending()));
        Assert.Equal("model", message.AuthorName);
        Assert.Collection(
            message.Contents,
            content => Assert.IsType<TextReasoningContent>(content),
            content => Assert.Equal("a", Assert.IsType<TextContent>(content).Text),
            content => Assert.IsType<FunctionCallContent>(content),
            content => Assert.Equal("b", Assert.IsType<TextContent>(content).Text)
        );
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(87)]
    public void AppendText_PartitionAndAcknowledgementBoundaries_PreserveOneMessage(int batchSize)
    {
        var projection = CreateProjection();
        const string expected = "The user wants to fix `pnpm test`.\nKeep every space.";
        Guid? id = null;
        var index = 0;
        foreach (var character in expected)
        {
            projection.Apply(Append(character.ToString()));
            if (++index % batchSize != 0)
                continue;
            var snapshots = projection.CapturePending();
            id ??= Assert.Single(snapshots).MessageId;
            Assert.Equal(id, Assert.Single(snapshots).MessageId);
            projection.Acknowledge(snapshots);
        }
        projection.SealOpen(ConversationMessageState.Completed);

        var final = Assert.Single(projection.CapturePending());
        Assert.Equal(expected, Deserialize(final).Text);
        if (id != null)
            Assert.Equal(id, final.MessageId);
        Assert.Equal(ConversationMessageState.Completed, final.State);
    }

    [Fact]
    public void PutMessage_ShrinkingAndReordering_ReplacesIncludingDeletedBlocks()
    {
        var projection = CreateProjection();
        projection.Apply(Append("old"));
        projection.Apply(
            new AgentMessageOperation
            {
                SourceId = "message",
                Kind = AgentMessageOperationKind.PutMessage,
                Header = new ChatMessage(
                    ChatRole.Assistant,
                    [new TextReasoningContent("reason"), new TextContent("a")]
                ),
            }
        );
        projection.Apply(Append("b") with { BlockId = "block:1" });

        var message = Deserialize(Assert.Single(projection.CapturePending()));
        Assert.Collection(
            message.Contents,
            content => Assert.Equal("reason", Assert.IsType<TextReasoningContent>(content).Text),
            content => Assert.Equal("ab", Assert.IsType<TextContent>(content).Text)
        );
    }

    [Fact]
    public void Acknowledge_EarlierCapture_DoesNotClearNewerChange()
    {
        var projection = CreateProjection();
        projection.Apply(Append("a"));
        var first = projection.CapturePending();
        projection.Apply(Append("b"));

        projection.Acknowledge(first);

        Assert.Equal("ab", Deserialize(Assert.Single(projection.CapturePending())).Text);
        Assert.Equal("a", Deserialize(Assert.Single(first)).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexDeltas_RepeatedTextAndWhitespace_AppendWithoutLosingEarlierContent(bool thinking)
    {
        var adapter = new CodexMessageAdapter();
        var projection = CreateProjection();
        var parts = new[] { "a", "a", " ", "b" };
        for (var index = 0; index < parts.Length; index++)
            foreach (
                var operation in adapter.Map(
                    new AgentResponseUpdate(
                        ChatRole.Assistant,
                        [thinking ? new TextReasoningContent(parts[index]) : new TextContent(parts[index])]
                    )
                    {
                        MessageId = "item_0",
                        AuthorName = "codex",
                        AdditionalProperties = new()
                        {
                            ["type"] = index == parts.Length - 1 ? "item.completed" : "item.updated",
                        },
                    }
                )
            )
                projection.Apply(operation);

        var snapshot = Assert.Single(projection.CapturePending());
        var content = Assert.Single(Deserialize(snapshot).Contents);
        Assert.Equal(
            "aa b",
            thinking ? Assert.IsType<TextReasoningContent>(content).Text : Assert.IsType<TextContent>(content).Text
        );
        Assert.Equal(ConversationMessageState.Completed, snapshot.State);
    }

    [Fact]
    public void CodexItems_DistinctCompletedMessages_PreserveEachMessage()
    {
        var adapter = new CodexMessageAdapter();
        var projection = CreateProjection();
        foreach (var id in new[] { "item_0", "item_1" })
        foreach (
            var operation in adapter.Map(
                new AgentResponseUpdate(ChatRole.Assistant, "same text")
                {
                    MessageId = id,
                    AuthorName = "codex",
                    AdditionalProperties = new() { ["type"] = "item.completed" },
                }
            )
        )
            projection.Apply(operation);

        var snapshots = projection.CapturePending();
        Assert.Equal(2, snapshots.Count);
        Assert.NotEqual(snapshots[0].MessageId, snapshots[1].MessageId);
        Assert.All(snapshots, snapshot => Assert.Equal("same text", Deserialize(snapshot).Text));
    }

    [Fact]
    public void CodexItems_TodoStatusChanges_KeepLatestState()
    {
        var adapter = new CodexMessageAdapter();
        var projection = CreateProjection();
        foreach (var text in new[] { "Todo list: pending", "Todo list: done" })
        foreach (
            var operation in adapter.Map(
                new AgentResponseUpdate(ChatRole.System, text)
                {
                    MessageId = "item_0",
                    AuthorName = "codex",
                    AdditionalProperties = new() { ["type"] = "item.updated", ["itemType"] = "TodoListItem" },
                }
            )
        )
            projection.Apply(operation);

        Assert.Equal("Todo list: done", Deserialize(Assert.Single(projection.CapturePending())).Text);
    }

    [Fact]
    public void Apply_RepeatedEventAndLateUpdate_DeduplicatesAndOpensNextMessage()
    {
        var projection = CreateProjection();
        var operation = Append("word") with { EventId = "event-1" };
        projection.Apply(operation);
        Assert.Null(projection.Apply(operation));
        projection.SealOpen(ConversationMessageState.Completed);

        projection.Apply(Append("late"));

        var messages = projection.CapturePending().Select(Deserialize).ToArray();
        Assert.Equal(["word", "late"], messages.Select(message => message.Text));
        Assert.Equal(2, messages.Select(message => message.MessageId).Distinct().Count());
        Assert.Equal(
            ["completed", "open"],
            messages.Select(message => message.AdditionalProperties!["messageState"]!.ToString())
        );
    }

    [Fact]
    public void AppendText_InterleavedMessagesAndBlocks_PreservesIdentityAndOrder()
    {
        var projection = CreateProjection();
        projection.Apply(Append("a"));
        projection.Apply(Append("second") with { SourceId = "other" });
        projection.Apply(
            Append(" reasoning") with
            {
                BlockId = "thinking",
                Content = new TextReasoningContent(" reasoning"),
            }
        );
        projection.Apply(Append("b"));

        var messages = projection.CapturePending().Select(Deserialize).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("ab", messages[0].Text);
        Assert.Equal(" reasoning", Assert.IsType<TextReasoningContent>(messages[0].Contents[1]).Text);
        Assert.Equal("second", messages[1].Text);
    }

    internal static AgentMessageProjection CreateProjection() =>
        new(
            new ConversationMessageWriteScope
            {
                ProjectId = Guid.NewGuid(),
                ContextId = "test",
                Generation = 0,
                ProducerId = Guid.NewGuid(),
            },
            TimeProvider.System
        );

    private static AgentMessageOperation Append(string text) =>
        new()
        {
            SourceId = "message",
            Header = new ChatMessage(ChatRole.Assistant, []),
            Kind = AgentMessageOperationKind.AppendText,
            BlockId = "text",
            Content = new TextContent(text),
        };

    private static ChatMessage Deserialize(ConversationMessageSnapshot snapshot) =>
        JsonSerializer.Deserialize<ChatMessage>(
            snapshot.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
}
