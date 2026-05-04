using Agw.Agents.Contracts;
using Agw.Shared.Contracts.Agents;
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
    public void Deserialize_SettingCommand_WithEnvironmentVariables_ReturnsEnvironmentVariables()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        const string json = """
                            {
                              "type": "SettingCommand",
                              "settingContent": "{}",
                              "environmentVariables": {
                                "AGW_TOKEN": "secret",
                                "EMPTY_VALUE": ""
                              },
                              "projectId": "__PROJECT_ID__",
                              "taskId": "__TASK_ID__"
                            }
                            """;
        var payload = json
            .Replace("__PROJECT_ID__", projectId.ToString())
            .Replace("__TASK_ID__", taskId.ToString());

        var request = JsonUtil.Deserialize<AgentRunCommand>(payload);

        var settingRequest = Assert.IsType<SettingCommand>(request);
        Assert.Equal("secret", settingRequest.EnvironmentVariables["AGW_TOKEN"]);
        Assert.Equal("", settingRequest.EnvironmentVariables["EMPTY_VALUE"]);
    }

    [Fact]
    public void Equals_WhenEnvironmentVariablesDiffer_ReturnsFalse()
    {
        var projectId = Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var left = CreateSettingCommand(projectId, taskId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "one"
        });
        var right = CreateSettingCommand(projectId, taskId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "two"
        });

        Assert.NotEqual(left, right);
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

    private static SettingCommand CreateSettingCommand(
        Guid projectId,
        Guid taskId,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        return new SettingCommand(
            projectId,
            taskId,
            settingContent: "{}",
            environmentVariables: new Dictionary<string, string>(environmentVariables));
    }
}
