using System.Text.Json;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests.Acp;

public sealed class AcpUpdateMapperTests
{
    [Fact]
    public void Map_MessagesWithoutMessageId_GroupsContiguousChunks()
    {
        var mapper = new AcpUpdateMapper("r1");
        AcpSessionUpdate[] chunks =
        [
            new AcpAgentMessageChunk(new AcpTextContent("a")),
            new AcpAgentMessageChunk(new AcpTextContent("b")),
            new AcpAgentThoughtChunk(new AcpTextContent("thought")),
            new AcpAgentMessageChunk(new AcpTextContent("c")),
            new AcpUserMessageChunk(new AcpTextContent("ignored")),
            new AcpAgentMessageChunk(new AcpTextContent("d")),
        ];

        var updates = chunks.SelectMany(mapper.Map).ToArray();

        Assert.Equal(
            ["r1-message-1", "r1-message-1", "r1-thought-1", "r1-message-2", "r1-message-3"],
            updates.Select(update => update.MessageId)
        );
        Assert.All(
            updates,
            update =>
            {
                Assert.Equal("acp", update.AuthorName);
                Assert.Equal("r1", update.ResponseId);
                Assert.Equal(ChatRole.Assistant, update.Role);
            }
        );
        Assert.Same(chunks[2], updates[2].RawRepresentation);
        Assert.Equal("thought", Assert.IsType<TextReasoningContent>(Assert.Single(updates[2].Contents)).Text);
    }

    [Fact]
    public void Map_MessagesWithMessageId_UsesPrefixedId()
    {
        var mapper = new AcpUpdateMapper("r1");

        var first = Assert.Single(mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("a"), "m-9")));
        var second = Assert.Single(mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("b"), "m-9")));

        Assert.Equal("acp:m-9", first.MessageId);
        Assert.Equal("acp:m-9", second.MessageId);
    }

    [Fact]
    public void Map_AssistantAttachments_ConvertsContentBlocks()
    {
        var mapper = new AcpUpdateMapper("r1");
        AcpContentBlock[] blocks =
        [
            new AcpImageContent("AQID", "image/png"),
            new AcpAudioContent("AQID", "audio/wav"),
            new AcpResourceLink("https://example.com/file.pdf", "file.pdf"),
            new AcpEmbeddedResource(new AcpResourceContents("agw:context/readme", Text: "embedded")),
            new AcpEmbeddedResource(new AcpResourceContents("agw:context/binary", Blob: "AQID")),
        ];

        var contents = blocks.Select(block =>
            Assert.Single(Assert.Single(mapper.Map(new AcpAgentMessageChunk(block))).Contents)
        );

        Assert.Collection(
            contents,
            content => Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<DataContent>(content).Data.ToArray()),
            content => Assert.Equal("audio/wav", Assert.IsType<DataContent>(content).MediaType),
            content => Assert.Equal(new Uri("https://example.com/file.pdf"), Assert.IsType<UriContent>(content).Uri),
            content => Assert.Equal("embedded", Assert.IsType<TextContent>(content).Text),
            content => Assert.Equal("application/octet-stream", Assert.IsType<DataContent>(content).MediaType)
        );
    }

    [Fact]
    public void Map_ToolCallRawInputArrivesLater_EmitsCallOnInProgress()
    {
        var mapper = new AcpUpdateMapper("r1");
        Assert.Empty(mapper.Map(new AcpToolCall("t1", "Read src/x.ts", "read", "pending")));
        var rawInput = JsonSerializer.SerializeToElement(new { path = "a" });

        var update = Assert.Single(mapper.Map(new AcpToolCallUpdate("t1", Status: "in_progress", RawInput: rawInput)));

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.Equal("t1", call.CallId);
        Assert.Equal("read", call.Name);
        Assert.Equal("a", call.Arguments!["path"]?.ToString());
        Assert.True(call.InformationalOnly);
        Assert.Equal("Read src/x.ts", call.AdditionalProperties!["toolTitle"]);
        Assert.Equal("r1-tool-t1", update.MessageId);
    }

    [Fact]
    public void Map_ToolUpdateForUnknownId_EmitsCallBeforeResult()
    {
        var mapper = new AcpUpdateMapper("r1");
        var rawInput = JsonSerializer.SerializeToElement("input");

        var updates = mapper.Map(new AcpToolCallUpdate("t1", Status: "completed", RawInput: rawInput));

        Assert.Collection(
            updates,
            update =>
            {
                var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
                Assert.Equal("other", call.Name);
                Assert.Equal("input", call.Arguments!["input"]?.ToString());
            },
            update =>
                Assert.Equal("completed", Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents)).Result)
        );
        var state = Assert.IsType<AcpToolCallState>(mapper.GetToolCall("t1"));
        Assert.True(state.CallEmitted);
        Assert.True(state.ResultEmitted);
        Assert.Empty(mapper.Map(new AcpToolCallUpdate("t1", Status: "completed")));
    }

    [Theory]
    [InlineData("old", "a/src/x.ts")]
    [InlineData(null, "/dev/null")]
    public void Map_ToolResultDiff_ProducesUnifiedDiff(string? oldText, string oldPath)
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(new AcpToolCall("t1", "Edit", Status: "in_progress"));
        var patch = new AcpToolCallUpdate(
            "t1",
            Status: "completed",
            Content: [new AcpToolCallDiff("src/x.ts", oldText, "new\n")]
        );

        var update = Assert.Single(mapper.Map(patch));

        var text = Assert.IsType<string>(Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents)).Result);
        Assert.StartsWith($"--- {oldPath}\n+++ b/src/x.ts\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("+new", text);
        if (oldText is not null)
        {
            Assert.Contains("-old", text);
        }
    }

    [Fact]
    public void Map_ToolResultFailed_ProducesNonfatalError()
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(new AcpToolCall("t1", "Read", "read", "in_progress"));

        var update = Assert.Single(mapper.Map(new AcpToolCallUpdate("t1", Status: "failed")));

        Assert.Equal("r1-tool-result-t1", update.MessageId);
        Assert.Equal(ChatRole.Tool, update.Role);
        Assert.Collection(
            update.Contents,
            content => Assert.Equal("failed", Assert.IsType<FunctionResultContent>(content).Result),
            content => Assert.IsType<ErrorContent>(content)
        );
    }

    [Fact]
    public void Map_ToolResultEmptyReplacementContent_UsesRawOutput()
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(
            new AcpToolCall(
                "t1",
                "Read",
                Status: "in_progress",
                Content: [new AcpToolCallContentBlock(new AcpTextContent("old"))]
            )
        );
        var rawOutput = JsonSerializer.SerializeToElement(new { n = 1 });

        var update = Assert.Single(
            mapper.Map(new AcpToolCallUpdate("t1", Status: "completed", Content: [], RawOutput: rawOutput))
        );

        Assert.Equal("""{"n":1}""", Assert.IsType<FunctionResultContent>(Assert.Single(update.Contents)).Result);
    }

    [Fact]
    public void EnsureToolCall_PermissionRequest_EmitsCallOnlyOnce()
    {
        var mapper = new AcpUpdateMapper("r1");
        var request = new AcpToolCallUpdate("t1", "Read", "read", "pending");

        var update = Assert.Single(mapper.EnsureToolCall(request));

        Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.Same(request, update.RawRepresentation);
        Assert.Empty(mapper.Map(new AcpToolCallUpdate("t1", Status: "in_progress")));
        Assert.Equal("Read", mapper.GetToolCall("t1")?.Title);
    }

    [Fact]
    public void Complete_PendingCall_EmitsCallAndErrorResult()
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(new AcpToolCall("t1", "Read", Status: "pending"));

        var updates = mapper.Complete();

        Assert.Collection(
            updates,
            update => Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents)),
            update =>
                Assert.Collection(
                    update.Contents,
                    content =>
                        Assert.Equal(
                            "The ACP agent ended the turn before this tool call finished.",
                            Assert.IsType<FunctionResultContent>(content).Result
                        ),
                    content => Assert.IsType<ErrorContent>(content)
                )
        );
        Assert.Empty(mapper.Complete());
    }

    [Fact]
    public void Map_PlanEntries_ProducesTodoSnapshot()
    {
        var mapper = new AcpUpdateMapper("r1");
        var plan = new AcpPlan([new("Review", "high", "completed"), new("Test", "medium", "in_progress")]);

        var update = Assert.Single(mapper.Map(plan));

        Assert.Equal("acp", update.AuthorName);
        Assert.Same(plan, update.RawRepresentation);
        Assert.Equal("r1-plan", update.MessageId);
        Assert.Equal("tool-todo-snapshot", update.AdditionalProperties!["type"]);
        Assert.Equal("acp_plan", update.AdditionalProperties["toolName"]);
        Assert.Equal("r1-plan", update.AdditionalProperties["callId"]);
        var items = JsonSerializer.SerializeToElement(update.AdditionalProperties["items"]);
        Assert.Equal("r1:0", items[0].GetProperty("id").GetString());
        Assert.Equal("Review", items[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("description").ValueKind);
        Assert.True(items[0].GetProperty("isComplete").GetBoolean());
        Assert.Equal("r1:1", items[1].GetProperty("id").GetString());
        Assert.False(items[1].GetProperty("isComplete").GetBoolean());
        Assert.Equal(update.MessageId, Assert.Single(mapper.Map(new AcpPlan([]))).MessageId);
    }

    [Fact]
    public void CreateResult_LastMessageAfterToolCall_PointsToSource()
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("before")));
        mapper.Map(new AcpToolCall("t1", "Read", Status: "in_progress"));
        Assert.Null(mapper.CreateResult());
        mapper.Map(new AcpToolCallUpdate("t1", Status: "completed"));
        var source = Assert.Single(mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("final "))));
        mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("answer")));
        mapper.Map(new AcpAgentThoughtChunk(new AcpTextContent("thought")));

        var result = Assert.IsType<Microsoft.Agents.AI.AgentResponseUpdate>(mapper.CreateResult());

        Assert.Equal("r1-result", result.MessageId);
        Assert.Equal("final answer", Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
        Assert.Equal("result", result.AdditionalProperties!["type"]);
        Assert.Equal(source.MessageId, result.AdditionalProperties["resultSourceMessageId"]);
    }

    [Fact]
    public void CreateResult_LatestMessageContainsOnlyImage_ReturnsNull()
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("before"), "text"));
        mapper.Map(new AcpAgentMessageChunk(new AcpImageContent("AQID", "image/png"), "image"));

        Assert.Null(mapper.CreateResult());
    }

    [Fact]
    public void CreateResult_PendingToolCall_UsesFollowingMessage()
    {
        var mapper = new AcpUpdateMapper("r1");
        mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("before")));
        mapper.Map(new AcpToolCall("t1", "Read", Status: "pending"));
        Assert.Null(mapper.CreateResult());
        var source = Assert.Single(mapper.Map(new AcpAgentMessageChunk(new AcpTextContent("final answer"))));

        mapper.Complete();
        var result = mapper.CreateResult();

        Assert.NotNull(result);
        Assert.Equal(source.MessageId, result.AdditionalProperties!["resultSourceMessageId"]);
    }

    [Fact]
    public void Map_UnknownUpdate_ReturnsEmpty() => Assert.Empty(new AcpUpdateMapper("r1").Map(new AcpSessionUpdate()));
}
