using Agw.Api.Contracts;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using Agw.Shared.Utils;

namespace Agw.Agents.Tests;

public class ExecutionRequestsTests
{
    [Fact]
    public void Deserialize_SettingCommand_ReturnsSettingCommand()
    {
        const string json = """
                            {
                              "type": "SettingCommand",
                              "settingContent": "{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}"
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var settingRequest = Assert.IsType<SettingCommand>(request);
        Assert.Equal("{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}", settingRequest.SettingContent);
    }

    [Fact]
    public void Deserialize_ExecCommand_WithoutSettingCommand_ReturnsExecutionCommand()
    {
        const string json = """
                            {
                              "type": "ExecCommand",
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

        var executionRequest = Assert.IsType<ExecCommand>(request);
        Assert.Equal(AgentRuntimeType.Agent, executionRequest.AgentType);
        Assert.Equal("session-1", executionRequest.SessionId);
        Assert.Null(executionRequest.TaskId);
        var textContent = Assert.IsType<AgwTextContent>(Assert.Single(executionRequest.Input.Contents));
        Assert.Equal("hello", textContent.Content);
    }

    [Fact]
    public void Deserialize_InterruptCommand_WithoutReason_ReturnsInterruptCommand()
    {
        const string json = """
                            {
                              "type": "InterruptCommand"
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var interruptRequest = Assert.IsType<InterruptCommand>(request);
        Assert.Null(interruptRequest.Reason);
    }
}
