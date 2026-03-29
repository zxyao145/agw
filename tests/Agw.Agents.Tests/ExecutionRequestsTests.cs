using Agw.Api.Contracts;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Utils;

namespace Agw.Agents.Tests;

public class ExecutionRequestsTests
{
    [Fact]
    public void Deserialize_SettingRequest_ReturnsSettingRequest()
    {
        const string json = """
                            {
                              "type": "SettingRequest",
                              "settingContent": "{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}"
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var settingRequest = Assert.IsType<SettingRequest>(request);
        Assert.Equal("{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}", settingRequest.SettingContent);
    }

    [Fact]
    public void Deserialize_ExecRequest_WithoutSettingRequest_ReturnsExecutionRequest()
    {
        const string json = """
                            {
                              "type": "ExecRequest",
                              "agentType": 0,
                              "input": {
                                "messageId": "msg-1",
                                "author": "$agw",
                                "contents": [
                                  {
                                    "type": "TextContent",
                                    "content": "hello"
                                  }
                                ]
                              },
                              "sessionId": "session-1",
                              "projectId": null,
                              "taskId": null
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var executionRequest = Assert.IsType<ExecRequest>(request);
        Assert.Equal(AgentRuntimeType.Agent, executionRequest.AgentType);
        Assert.Equal("session-1", executionRequest.SessionId);
        var textContent = Assert.IsType<AgwTextContent>(Assert.Single(executionRequest.Input.Contents));
        Assert.Equal("hello", textContent.Content);
    }

    [Fact]
    public void Deserialize_InterruptRequest_WithoutReason_ReturnsInterruptRequest()
    {
        const string json = """
                            {
                              "type": "InterruptRequest"
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var interruptRequest = Assert.IsType<InterruptRequest>(request);
        Assert.Null(interruptRequest.Reason);
    }
}
