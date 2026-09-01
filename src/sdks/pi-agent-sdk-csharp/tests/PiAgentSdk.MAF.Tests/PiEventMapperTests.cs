using System.Text.Json;
using Microsoft.Extensions.AI;
using PiAgentSdk.MAF.Internal;
using Xunit;

namespace PiAgentSdk.MAF.Tests;

public sealed class PiEventMapperTests
{
    [Fact]
    public void ToUpdate_ToolCallEnd_PreservesJsonArgumentTypesAndIsInformational()
    {
        // Arrange
        var arguments = JsonSerializer.SerializeToElement(new { count = 2, force = true });
        var evt = new PiMessageUpdateEvent
        {
            AssistantMessageEvent = new PiToolCallEndDelta
            {
                ToolCall = new PiToolCallContent
                {
                    Id = "call-1",
                    Name = "bash",
                    Arguments = arguments,
                },
            },
        };
        var mapper = new PiEventMapper();

        // Act
        var update = mapper.ToUpdate(evt);

        // Assert
        Assert.Equal("pi", update!.AuthorName);
        Assert.False(update.AdditionalProperties!.ContainsKey("agentName"));
        Assert.Equal(string.Empty, update.AdditionalProperties["modelName"]);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(update.Contents));
        Assert.True(call.InformationalOnly);
        Assert.Equal(JsonValueKind.Number, Assert.IsType<JsonElement>(call.Arguments!["count"]).ValueKind);
        Assert.Equal(JsonValueKind.True, Assert.IsType<JsonElement>(call.Arguments["force"]).ValueKind);
    }

    [Theory]
    [InlineData("tool_execution_start")]
    [InlineData("tool_execution_update")]
    public void ToUpdate_ToolExecutionProgress_DoesNotEmitRepeatedStatusText(string eventType)
    {
        // Arrange
        var evt = new PiToolExecutionEvent(eventType) { ToolCallId = "call-1", ToolName = "bash" };
        var mapper = new PiEventMapper();

        // Act
        var update = mapper.ToUpdate(evt);

        // Assert
        Assert.Null(update);
    }

    [Fact]
    public void ToUpdate_AssistantError_IsFatal()
    {
        // Arrange
        var evt = new PiMessageEvent("message_end")
        {
            Message = new PiAssistantMessage { StopReason = "error", ErrorMessage = "boom" },
        };
        var mapper = new PiEventMapper("configured-model");

        // Act
        var update = mapper.ToUpdate(evt);

        // Assert
        var error = Assert.IsType<ErrorContent>(Assert.Single(update!.Contents));
        Assert.True(Assert.IsType<bool>(error.AdditionalProperties!["isFatalError"]));
        Assert.Equal("pi", update.AuthorName);
        Assert.False(update.AdditionalProperties!.ContainsKey("agentName"));
        Assert.Equal("configured-model", update.AdditionalProperties["modelName"]);
    }

    [Fact]
    public void ToUpdate_TurnEnd_AggregatesAssistantAndToolUsageOnce()
    {
        // Arrange
        var evt = new PiTurnEndEvent
        {
            Message = new PiAssistantMessage
            {
                Model = "turn-model",
                Usage = new PiUsage
                {
                    Input = 10,
                    Output = 2,
                    Reasoning = 2,
                    TotalTokens = 12,
                },
            },
            ToolResults =
            [
                new PiToolResultMessage
                {
                    Usage = new PiUsage
                    {
                        Input = 3,
                        Output = 1,
                        Reasoning = 1,
                        TotalTokens = 4,
                    },
                },
            ],
        };
        var mapper = new PiEventMapper();

        // Act
        var update = mapper.ToUpdate(evt);

        // Assert
        var usage = Assert.IsType<UsageContent>(Assert.Single(update!.Contents));
        Assert.Equal(13, usage.Details.InputTokenCount);
        Assert.Equal(3, usage.Details.OutputTokenCount);
        Assert.Equal(3, usage.Details.ReasoningTokenCount);
        Assert.Equal(16, usage.Details.TotalTokenCount);
        Assert.Equal("pi", update.AuthorName);
        Assert.False(update.AdditionalProperties!.ContainsKey("agentName"));
        Assert.Equal("turn-model", update.AdditionalProperties["modelName"]);
    }

    [Fact]
    public void ToUpdate_TurnEndWithoutDeltas_EmitsAuthoritativeAssistantTextAndUsage()
    {
        // Arrange
        var evt = new PiTurnEndEvent
        {
            Message = new PiAssistantMessage
            {
                Content = [new PiTextContent { Text = "authoritative" }],
                Usage = new PiUsage
                {
                    Input = 2,
                    Output = 1,
                    TotalTokens = 3,
                },
            },
        };
        var mapper = new PiEventMapper();

        // Act
        var update = mapper.ToUpdate(evt);

        // Assert
        Assert.Equal(ChatRole.Assistant, update!.Role);
        Assert.Equal("authoritative", Assert.IsType<TextContent>(update.Contents[0]).Text);
        Assert.IsType<UsageContent>(update.Contents[1]);
    }

    [Fact]
    public void ToUpdate_ConsecutiveAuthoritativeTurnFallbacks_UseDistinctMessageIds()
    {
        // Arrange
        var mapper = new PiEventMapper();
        var first = new PiTurnEndEvent
        {
            Message = new PiAssistantMessage { Content = [new PiTextContent { Text = "first" }] },
        };
        var second = new PiTurnEndEvent
        {
            Message = new PiAssistantMessage { Content = [new PiTextContent { Text = "second" }] },
        };

        // Act
        var firstUpdate = mapper.ToUpdate(first);
        var secondUpdate = mapper.ToUpdate(second);

        // Assert
        Assert.NotEqual(firstUpdate!.MessageId, secondUpdate!.MessageId);
    }

    [Fact]
    public void ToHistoryMessages_InvalidAssistantImage_ThrowsProtocolException()
    {
        // Arrange
        var evt = new PiTurnEndEvent
        {
            Message = new PiAssistantMessage
            {
                Content = [new PiImageContent { Data = "not-base64", MimeType = "image/png" }],
            },
        };

        // Act & Assert
        Assert.Throws<PiProtocolException>(() => PiEventMapper.ToHistoryMessages(evt));
    }

    [Fact]
    public void ToHistoryMessages_AssistantAndTool_UseCanonicalMetadata()
    {
        // Arrange
        var evt = new PiTurnEndEvent
        {
            Message = new PiAssistantMessage { Model = "turn-model", Content = [new PiTextContent { Text = "done" }] },
            ToolResults =
            [
                new PiToolResultMessage
                {
                    ToolCallId = "call-1",
                    ToolName = "bash",
                    Content = [new PiTextContent { Text = "result" }],
                },
            ],
        };

        // Act
        var messages = PiEventMapper.ToHistoryMessages(evt);

        // Assert
        Assert.All(
            messages,
            message =>
            {
                Assert.Equal("pi", message.AuthorName);
                Assert.False(message.AdditionalProperties!.ContainsKey("agentName"));
                Assert.Equal("turn-model", message.AdditionalProperties["modelName"]);
            }
        );
    }
}
