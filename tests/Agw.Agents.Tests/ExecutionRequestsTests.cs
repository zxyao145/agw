using Agw.Api.Contracts;
using Agw.Shared.Models;
using Agw.Shared.Utils;

namespace Agw.Agents.Tests;

public class ExecutionRequestsTests
{
    [Fact]
    public void Deserialize_SettingCommand_ReturnsSettingCommand()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        const string json = """
                            {
                              "type": "SettingCommand",
                              "settingContent": "{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}",
                              "projectId": "__PROJECT_ID__",
                              "taskId": "__TASK_ID__"
                            }
                            """;
        var payload = json
            .Replace("__PROJECT_ID__", projectId.ToString())
            .Replace("__TASK_ID__", taskId.ToString());

        var request = JsonUtil.Deserialize<AgentRunCommand>(payload);

        var settingRequest = Assert.IsType<SettingCommand>(request);
        Assert.Equal("{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}", settingRequest.SettingContent);
        Assert.Equal(projectId, settingRequest.ProjectId);
        Assert.Equal(taskId, settingRequest.TaskId);
    }

    [Fact]
    public void Deserialize_ExecCommand_ReturnsExecutionCommand()
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
                              }
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var executionRequest = Assert.IsType<ExecCommand>(request);
        Assert.Equal(AgentRuntimeType.Agent, executionRequest.AgentType);
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
