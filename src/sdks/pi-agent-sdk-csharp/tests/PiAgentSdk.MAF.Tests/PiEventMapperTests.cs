using System.Text.Json;
using Microsoft.Extensions.AI;
using PiAgentSdk.MAF.Internal;
using Xunit;

namespace PiAgentSdk.MAF.Tests;

public sealed class PiEventMapperTests
{
    [Fact]
    public void ToHistoryMessages_StreamedContent_SharesIdentityWithoutEmittingAgain()
    {
        var mapper = new PiEventMapper();
        var assistant = new PiAssistantMessage { Content = [new PiTextContent { Text = "complete" }] };
        mapper.ToUpdate(new PiMessageEvent("message_start") { Message = assistant });
        var delta = mapper.ToUpdate(
            new PiMessageUpdateEvent
            {
                AssistantMessageEvent = new PiTextDelta { Delta = "complete", ContentIndex = 0 },
            }
        );
        Assert.Null(mapper.ToUpdate(new PiMessageEvent("message_end") { Message = assistant }));
        var turn = new PiTurnEndEvent { Message = assistant };

        var history = mapper.ToHistoryMessages(turn);
        var update = mapper.ToUpdate(turn);

        Assert.NotNull(delta);
        Assert.Null(update);
        Assert.Equal(delta.MessageId, Assert.Single(history).MessageId);
        Assert.Equal("block:0", delta.Contents[0].AdditionalProperties!["blockId"]);
        Assert.Equal("complete", Assert.Single(history).Text);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("STOP")]
    public void ToUpdate_CustomMessageAfterAgentEnd_PreservesResult(string stopReason)
    {
        // Arrange
        var mapper = new PiEventMapper();
        mapper.ToUpdate(
            new PiAgentEndEvent
            {
                Messages =
                [
                    new PiAssistantMessage { StopReason = stopReason, Content = [new PiTextContent { Text = "done" }] },
                ],
            }
        );
        mapper.ToUpdate(
            new PiMessageEvent("message_start")
            {
                Message = new PiUnknownMessage("custom", JsonSerializer.SerializeToElement(new { role = "custom" })),
            }
        );

        // Act
        var result = mapper.ToUpdate(new PiMarkerEvent("agent_settled"));

        // Assert
        Assert.NotNull(result);
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
    }

    [Fact]
    public void ToUpdate_AgentSettled_EmitsFinalTextOnceWithSeparateIdentityAndNoUsage()
    {
        // Arrange
        var mapper = new PiEventMapper("configured-model");
        var delta = mapper.ToUpdate(
            new PiMessageUpdateEvent
            {
                AssistantMessageEvent = new PiTextDelta { Delta = "done", ContentIndex = 0 },
            }
        );
        var assistant = new PiAssistantMessage
        {
            Model = "reported-model",
            StopReason = "stop",
            Content = [new PiThinkingContent { Thinking = "private reasoning" }, new PiTextContent { Text = "done" }],
            Usage = new PiUsage { TotalTokens = 12 },
        };
        mapper.ToUpdate(new PiTurnEndEvent { Message = assistant });

        // Act
        var end = mapper.ToUpdate(new PiAgentEndEvent { Messages = [assistant] });
        var result = mapper.ToUpdate(new PiMarkerEvent("agent_settled"));

        // Assert
        Assert.Null(end);
        Assert.NotNull(result);
        Assert.Equal("result", result.AdditionalProperties!["type"]);
        Assert.Equal("reported-model", result.AdditionalProperties["modelName"]);
        Assert.Equal("pi", result.AuthorName);
        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.Single(result.Contents)).Text);
        Assert.Equal(delta!.ResponseId, result.ResponseId);
        Assert.NotEqual(delta.MessageId, result.MessageId);
        Assert.Null(mapper.ToUpdate(new PiMarkerEvent("agent_settled")));
    }

    [Theory]
    [InlineData("toolUse")]
    [InlineData("error")]
    [InlineData("aborted")]
    public void ToUpdate_AgentEndsWithoutSuccessfulAnswer_DoesNotEmitResult(string stopReason)
    {
        // Arrange
        var mapper = new PiEventMapper();
        mapper.ToUpdate(
            new PiAgentEndEvent
            {
                Messages =
                [
                    new PiAssistantMessage
                    {
                        StopReason = stopReason,
                        Content = [new PiTextContent { Text = "partial" }],
                    },
                ],
            }
        );

        // Act
        var result = mapper.ToUpdate(new PiMarkerEvent("agent_settled"));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToUpdate_Continuation_UsesOnlyLastPassAnswer()
    {
        // Arrange
        var mapper = new PiEventMapper();
        mapper.ToUpdate(
            new PiAgentEndEvent
            {
                Messages =
                [
                    new PiAssistantMessage { StopReason = "stop", Content = [new PiTextContent { Text = "first" }] },
                ],
            }
        );
        mapper.ToUpdate(new PiMarkerEvent("agent_start"));
        mapper.ToUpdate(
            new PiTurnEndEvent
            {
                Message = new PiAssistantMessage
                {
                    StopReason = "toolUse",
                    Content = [new PiTextContent { Text = "working" }],
                },
            }
        );
        var final = new PiAssistantMessage { StopReason = "stop", Content = [new PiTextContent { Text = "final" }] };
        mapper.ToUpdate(new PiTurnEndEvent { Message = final });
        mapper.ToUpdate(new PiAgentEndEvent { Messages = [final] });

        // Act
        var result = mapper.ToUpdate(new PiMarkerEvent("agent_settled"));

        // Assert
        Assert.Equal("final", Assert.IsType<TextContent>(Assert.Single(result!.Contents)).Text);
    }

    [Theory]
    [InlineData("agent_start")]
    [InlineData("turn_start")]
    [InlineData("auto_retry_start")]
    [InlineData("retry.failed")]
    [InlineData("compaction.failed")]
    [InlineData("willRetry")]
    public void ToUpdate_InterruptedFinalAnswer_DoesNotEmitStaleResult(string interruption)
    {
        // Arrange
        var mapper = new PiEventMapper();
        mapper.ToUpdate(
            new PiAgentEndEvent
            {
                Messages =
                [
                    new PiAssistantMessage { StopReason = "stop", Content = [new PiTextContent { Text = "stale" }] },
                ],
            }
        );
        PiEvent evt = interruption switch
        {
            "retry.failed" => new PiRetryEvent("auto_retry_end") { Success = false, FinalError = "failed" },
            "compaction.failed" => new PiCompactionEvent("compaction_end") { ErrorMessage = "failed" },
            "willRetry" => new PiAgentEndEvent { WillRetry = true },
            _ => new PiMarkerEvent(interruption),
        };
        mapper.ToUpdate(evt);

        // Act
        var result = mapper.ToUpdate(new PiMarkerEvent("agent_settled"));

        // Assert
        Assert.Null(result);
    }

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
        Assert.Throws<PiProtocolException>(() => new PiEventMapper().ToHistoryMessages(evt));
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
        var messages = new PiEventMapper().ToHistoryMessages(evt);

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
