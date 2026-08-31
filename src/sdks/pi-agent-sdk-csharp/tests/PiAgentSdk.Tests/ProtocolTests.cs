using System.Text.Json;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Deserialize_TurnEnd_PreservesAssistantErrorAndTypedToolArguments()
    {
        // Arrange
        const string json = """
            {
              "type":"turn_end",
              "message":{
                "role":"assistant",
                "content":[{"type":"toolCall","id":"call-1","name":"bash","arguments":{"count":2,"force":true}}],
                "usage":{"input":10,"output":3,"cacheRead":1,"cacheWrite":2,"totalTokens":16},
                "stopReason":"error",
                "errorMessage":"provider failed",
                "timestamp":1
              },
              "toolResults":[]
            }
            """;

        // Act
        var evt = JsonSerializer.Deserialize<PiEvent>(json, PiProtocolJson.Options);

        // Assert
        var turn = Assert.IsType<PiTurnEndEvent>(evt);
        var assistant = Assert.IsType<PiAssistantMessage>(turn.Message);
        Assert.Equal("provider failed", assistant.ErrorMessage);
        var call = Assert.IsType<PiToolCallContent>(Assert.Single(assistant.Content));
        Assert.Equal(2, call.Arguments.GetProperty("count").GetInt32());
        Assert.True(call.Arguments.GetProperty("force").GetBoolean());
    }

    [Fact]
    public void Deserialize_UnknownEventAndMessage_PreserveRawJson()
    {
        // Arrange
        const string eventJson = """{"type":"future_event","value":42}""";
        const string messageJson = """{"role":"futureRole","value":"kept","timestamp":2}""";

        // Act
        var evt = JsonSerializer.Deserialize<PiEvent>(eventJson, PiProtocolJson.Options);
        var message = JsonSerializer.Deserialize<PiMessage>(messageJson, PiProtocolJson.Options);

        // Assert
        Assert.Equal(42, Assert.IsType<PiUnknownEvent>(evt).Raw.GetProperty("value").GetInt32());
        Assert.Equal("kept", Assert.IsType<PiUnknownMessage>(message).Raw.GetProperty("value").GetString());
    }

    [Fact]
    public void Deserialize_ToolResultImageAndNullableExitCode_ArePreserved()
    {
        // Arrange
        const string toolJson = """
            {"role":"toolResult","toolCallId":"call-1","toolName":"read","content":[{"type":"image","data":"AQID","mimeType":"image/png"}],"isError":false,"timestamp":1}
            """;
        const string bashJson = """
            {"role":"bashExecution","command":"sleep 1","output":"","cancelled":true,"truncated":false,"timestamp":1}
            """;

        // Act
        var tool = JsonSerializer.Deserialize<PiMessage>(toolJson, PiProtocolJson.Options);
        var bash = JsonSerializer.Deserialize<PiMessage>(bashJson, PiProtocolJson.Options);

        // Assert
        Assert.Equal(
            "AQID",
            Assert.IsType<PiImageContent>(Assert.Single(Assert.IsType<PiToolResultMessage>(tool).Content)).Data
        );
        Assert.Null(Assert.IsType<PiBashExecutionMessage>(bash).ExitCode);
    }
}
