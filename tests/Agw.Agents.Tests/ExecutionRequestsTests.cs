using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Agents.Dtos;
using Agw.Agents.Execution.Contracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Utils;

namespace Agw.Agents.Tests;

public class ExecutionRequestsTests
{
    [Fact]
    public void Deserialize_SettingCommand_ReturnsSettingCommand()
    {
        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid().ToString("D");
        const string json = """
                            {
                              "type": "SettingCommand",
                              "settingContent": "{\"workingDirectory\":\"D:/source/repos/agw\",\"maxTurns\":3}",
                              "projectId": "__PROJECT_ID__",
                              "contextId": "__CONTEXT_ID__"
                            }
                            """;
        var payload = json
            .Replace("__PROJECT_ID__", projectId.ToString())
            .Replace("__CONTEXT_ID__", contextId);

        var request = JsonUtil.Deserialize<AgentRunCommand>(payload);

        var settingRequest = Assert.IsType<SettingCommand>(request);
        Assert.Equal(projectId, settingRequest.ProjectId);
        Assert.Equal(contextId, settingRequest.ContextId);
    }

    [Fact]
    public void Deserialize_SettingCommand_WithEnvironmentVariables_ReturnsEnvironmentVariables()
    {
        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid().ToString("D");
        const string json = """
                            {
                              "type": "SettingCommand",
                              "settingContent": "{}",
                              "environmentVariables": {
                                "AGW_TOKEN": "secret",
                                "EMPTY_VALUE": ""
                              },
                              "projectId": "__PROJECT_ID__",
                              "contextId": "__CONTEXT_ID__"
                            }
                            """;
        var payload = json
            .Replace("__PROJECT_ID__", projectId.ToString())
            .Replace("__CONTEXT_ID__", contextId);

        var request = JsonUtil.Deserialize<AgentRunCommand>(payload);

        var settingRequest = Assert.IsType<SettingCommand>(request);
        Assert.Equal("secret", settingRequest.EnvironmentVariables["AGW_TOKEN"]);
        Assert.Equal("", settingRequest.EnvironmentVariables["EMPTY_VALUE"]);
    }

    [Fact]
    public void Deserialize_SettingCommand_IgnoresResume()
    {
        var projectId = Guid.NewGuid();
        var payload = $$"""
                        {
                          "type": "SettingCommand",
                          "projectId": "{{projectId}}",
                          "resume": true
                        }
                        """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(payload);

        var settingRequest = Assert.IsType<SettingCommand>(request);
        Assert.False(settingRequest.Resume);
    }

    [Fact]
    public void Equals_WhenOnlyResumeDiffers_ReturnsTrue()
    {
        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid().ToString("D");
        var left = new SettingCommand(projectId, contextId: contextId) { Resume = false };
        var right = new SettingCommand(projectId, contextId: contextId) { Resume = true };

        Assert.Equal(left, right);
    }

    [Fact]
    public void Equals_WhenEnvironmentVariablesDiffer_ReturnsFalse()
    {
        var projectId = Guid.NewGuid();
        var contextId = Guid.NewGuid().ToString("D");

        var left = CreateSettingCommand(projectId, contextId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "one"
        });
        var right = CreateSettingCommand(projectId, contextId, new Dictionary<string, string>
        {
            ["AGW_TOKEN"] = "two"
        });

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void Deserialize_ExecCommand_ReturnsExecutionCommand()
    {
        var agentId = Guid.NewGuid();
        const string json = """
                            {
                              "type": "ExecCommand",
                              "agentId": "__AGENT_ID__",
                              "agentType": 0,
                              "stream": false,
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

        var request = JsonUtil.Deserialize<AgentRunCommand>(
            json.Replace("__AGENT_ID__", agentId.ToString("D")));

        var executionRequest = Assert.IsType<ExecCommand>(request);
        Assert.Equal(agentId, executionRequest.AgentId);
        Assert.Equal(AgentRuntimeType.Agent, executionRequest.AgentType);
        Assert.False(executionRequest.Stream);
        var textContent = Assert.IsType<AgwTextContent>(Assert.Single(executionRequest.Input.Contents));
        Assert.Equal("hello", textContent.Content);
    }

    [Fact]
    public void Deserialize_LegacyExecCommand_DefaultsToStreamingWithoutAgentId()
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
        Assert.Null(executionRequest.AgentId);
        Assert.True(executionRequest.Stream);
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

    [Fact]
    public void Deserialize_HumanResponseCommand_ReturnsHumanResponseCommand()
    {
        const string json = """
                            {
                              "type": "HumanResponseCommand",
                              "requestId": "human-approval-1",
                              "approved": true,
                              "responseText": "Approved for translation."
                            }
                            """;

        var request = JsonUtil.Deserialize<AgentRunCommand>(json);

        var humanResponse = Assert.IsType<HumanResponseCommand>(request);
        Assert.Equal("human-approval-1", humanResponse.RequestId);
        Assert.True(humanResponse.Approved);
        Assert.Equal("Approved for translation.", humanResponse.ResponseText);
    }

    private static SettingCommand CreateSettingCommand(
        Guid projectId,
        string contextId,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        return new SettingCommand(
            projectId,
            environmentVariables: new Dictionary<string, string>(environmentVariables),
            contextId: contextId);
    }
}
