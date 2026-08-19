using System.Text.Json;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Shared.Tests;

public sealed class AgentRunResponseUpdateExtensionsTests
{
    [Fact]
    public void ToAiMessage_ChatMessageWithBlankTextualContent_RemovesBlankContent()
    {
        var message = new ChatMessage(
            ChatRole.Assistant,
            [
                new TextContent(" "),
                new TextReasoningContent(string.Empty),
                new TextReasoningContent("kept reasoning"),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()),
            ]
        );

        var result = message.ToAiMessage();

        Assert.NotNull(result);
        Assert.Collection(
            result.Contents,
            content => Assert.Equal("kept reasoning", Assert.IsType<AgwTextReasoningContent>(content).Content),
            content =>
                Assert.Equal("call-1", Assert.IsType<AgwFunctionCallContent>(content).AdditionalProperties!["callId"])
        );
    }

    [Fact]
    public void ToAiMessage_ResponseUpdateWithEmptyTextualContent_RemovesEmptyContent()
    {
        var update = new AgentResponseUpdate(
            ChatRole.Assistant,
            [
                new TextContent(string.Empty),
                new TextReasoningContent(string.Empty),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()),
            ]
        );

        var result = update.ToAiMessage();

        var content = Assert.IsType<AgwFunctionCallContent>(Assert.Single(result!.Contents));
        Assert.Equal("call-1", content.AdditionalProperties!["callId"]);
    }

    [Fact]
    public void ToAiMessage_ResponseUpdateWithWhitespaceOnlyTextualContent_PreservesContent()
    {
        var update = new AgentResponseUpdate(
            ChatRole.Assistant,
            [new TextContent("\n"), new TextContent("  "), new TextContent("\t"), new TextReasoningContent("\n\t")]
        );

        var result = update.ToAiMessage();

        Assert.NotNull(result);
        Assert.Collection(
            result.Contents,
            content => Assert.Equal("\n", Assert.IsType<AgwTextContent>(content).Content),
            content => Assert.Equal("  ", Assert.IsType<AgwTextContent>(content).Content),
            content => Assert.Equal("\t", Assert.IsType<AgwTextContent>(content).Content),
            content => Assert.Equal("\n\t", Assert.IsType<AgwTextReasoningContent>(content).Content)
        );
    }

    [Fact]
    public void ToAiMessage_EmptyToolStateText_PreservesSemanticContent()
    {
        var message = new ChatMessage(ChatRole.System, [new TextContent(string.Empty)])
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = ToolMessageTypes.TodoSnapshot },
        };

        var result = message.ToAiMessage();

        var content = Assert.IsType<AgwTextContent>(Assert.Single(result!.Contents));
        Assert.Equal(string.Empty, content.Content);
    }

    [Fact]
    public void ToAiMessage_StringFunctionResult_PreservesPlainText()
    {
        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", "Mode changed to \"execute\".")]
        );

        var result = message.ToAiMessage();

        var content = Assert.IsType<AgwFunctionResultContent>(Assert.Single(result!.Contents));
        Assert.Equal("Mode changed to \"execute\".", content.Content);
    }

    [Fact]
    public void ToAiMessage_JsonElementStringFunctionResult_PreservesPlainText()
    {
        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent("call-1", JsonSerializer.SerializeToElement("Mode changed to \"plan\"."))]
        );

        var result = message.ToAiMessage();

        var content = Assert.IsType<AgwFunctionResultContent>(Assert.Single(result!.Contents));
        Assert.Equal("Mode changed to \"plan\".", content.Content);
    }

    [Fact]
    public void ToAiMessage_StructuredFunctionResult_SerializesJson()
    {
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", new { mode = "execute" })]);

        var result = message.ToAiMessage();

        var content = Assert.IsType<AgwFunctionResultContent>(Assert.Single(result!.Contents));
        Assert.Equal("{\"mode\":\"execute\"}", content.Content);
    }
}
