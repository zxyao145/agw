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
            projection.Append(adapter.Map(update)!);

        var message = Assert.Single(projection.ReadMessages());
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
    public void Append_PartitionAndAcknowledgementBoundaries_PreserveOneMessage(int batchSize)
    {
        var projection = CreateProjection();
        const string expected = "The user wants to fix `pnpm test`.\nKeep every space.";
        string? id = null;
        var index = 0;
        foreach (var character in expected)
        {
            var delta = projection.Append(Message(character.ToString()));
            id ??= delta.MessageId;
            Assert.Equal(id, delta.MessageId);
            Assert.Equal(character.ToString(), delta.Text);
            if (++index % batchSize == 0)
                projection.Acknowledge(projection.CapturePending());
        }

        var final = Assert.Single(projection.ReadMessages());
        Assert.Equal(expected, final.Text);
        Assert.Equal(id, final.MessageId);
        Assert.False(ConversationHistoryMetadata.IsModelHistoryExcluded(final));
    }

    [Fact]
    public void Acknowledge_EarlierCapture_DoesNotClearNewerChange()
    {
        var projection = CreateProjection();
        projection.Append(Message("a"));
        var first = projection.CapturePending();
        projection.Append(Message("b"));

        projection.Acknowledge(first);

        Assert.Equal("ab", Deserialize(Assert.Single(projection.CapturePending())).Text);
        Assert.Equal("a", Deserialize(Assert.Single(first)).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodexDeltas_RepeatedTextAndWhitespace_AppendEveryContent(bool thinking)
    {
        var adapter = new CodexMessageAdapter();
        var projection = CreateProjection();
        var parts = new[] { "a", "a", " ", "b" };
        foreach (var part in parts)
            projection.Append(
                adapter.Map(
                    new AgentResponseUpdate(
                        ChatRole.Assistant,
                        [thinking ? new TextReasoningContent(part) : new TextContent(part)]
                    )
                    {
                        MessageId = "item_0",
                        AuthorName = "codex",
                        AdditionalProperties = new() { ["type"] = "item.updated" },
                    }
                )!
            );

        var content = Assert.Single(Assert.Single(projection.ReadMessages()).Contents);
        Assert.Equal(
            "aa b",
            thinking ? Assert.IsType<TextReasoningContent>(content).Text : Assert.IsType<TextContent>(content).Text
        );
    }

    [Fact]
    public void CodexItems_DistinctMessages_PreserveEachMessage()
    {
        var adapter = new CodexMessageAdapter();
        var projection = CreateProjection();
        foreach (var id in new[] { "item_0", "item_1" })
            projection.Append(
                adapter.Map(
                    new AgentResponseUpdate(ChatRole.Assistant, "same text")
                    {
                        MessageId = id,
                        AuthorName = "codex",
                        AdditionalProperties = new() { ["type"] = "item.completed" },
                    }
                )!
            );

        var snapshots = projection.CapturePending();
        Assert.Equal(2, snapshots.Count);
        Assert.NotEqual(snapshots[0].MessageId, snapshots[1].MessageId);
        Assert.All(snapshots, snapshot => Assert.Equal("same text", Deserialize(snapshot).Text));
    }

    [Fact]
    public void Append_InterleavedMessagesAndBlocks_PreservesIdentityAndOrder()
    {
        var projection = CreateProjection();
        projection.Append(Message("a"));
        projection.Append(Message("second", messageId: "other"));
        projection.Append(Message("other block", blockId: "other"));
        projection.Append(Message("b"));

        var messages = projection.ReadMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal(["ab", "other block"], messages[0].Contents.OfType<TextContent>().Select(content => content.Text));
        Assert.Equal("second", messages[1].Text);
    }

    [Fact]
    public void Append_ToolAndMediaContents_PreservesEachOccurrence()
    {
        var projection = CreateProjection();
        var message = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call", "lookup"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]
        )
        {
            MessageId = "message",
        };
        projection.Append(message);
        projection.Append(message);

        var contents = Assert.Single(projection.ReadMessages()).Contents;
        Assert.Equal(2, contents.OfType<FunctionCallContent>().Count());
        Assert.All(contents.OfType<DataContent>(), media => Assert.Equal(new byte[] { 1, 2, 3 }, media.Data.ToArray()));
        Assert.Equal(2, contents.OfType<DataContent>().Count());
    }

    [Fact]
    public void Append_ResultWithSharedSourceId_KeepsSeparateIdentityAndSourceReference()
    {
        var projection = CreateProjection();
        var body = projection.Append(Message("answer"));
        var result = Message("answer");
        result.AdditionalProperties = new() { ["type"] = "result", ["resultSourceMessageId"] = "message" };

        var delivered = projection.Append(result);

        Assert.NotEqual(body.MessageId, delivered.MessageId);
        Assert.Equal(body.MessageId, delivered.AdditionalProperties!["resultSourceMessageId"]);
        Assert.Equal(2, projection.ReadMessages().Count);
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

    private static ChatMessage Message(string text, string blockId = "text", string messageId = "message") =>
        new(ChatRole.Assistant, [new TextContent(text) { AdditionalProperties = new() { ["blockId"] = blockId } }])
        {
            MessageId = messageId,
        };

    private static ChatMessage Deserialize(ConversationMessageSnapshot snapshot) =>
        JsonSerializer.Deserialize<ChatMessage>(
            snapshot.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;
}
