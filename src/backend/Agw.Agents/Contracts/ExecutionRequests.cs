using Agw.Shared;
using Agw.Shared.Enums;
using Agw.Shared.Models;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Agw.Api.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SettingCommand), nameof(SettingCommand))]
[JsonDerivedType(typeof(ExecCommand), nameof(ExecCommand))]
[JsonDerivedType(typeof(InterruptCommand), nameof(InterruptCommand))]
public abstract class AgentRunCommand;

public class SettingCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public SettingCommand(
        Guid projectId,
        Guid? taskId = null,
        string? sessionId = null,
        string settingContent = "{}",
        bool resume = false)
    {
        if (resume && !taskId.HasValue)
        {
            throw new ArgumentException("taskId cannot be null when resume is true", nameof(taskId));
        }
        taskId ??= Guid.NewGuid();

        SettingContent = settingContent;
        ProjectId = projectId;
        TaskId = taskId;
        Resume = resume;
        if (string.IsNullOrEmpty(sessionId) && TaskId.HasValue)
        {
            SessionId = TaskId.Value.Normalize();
        }
        else
        {
            SessionId = sessionId;
        }
    }

    public required string SettingContent { get; set; }

    public required Guid ProjectId { get; set; }

    public Guid? TaskId { get; set; }

    public string? SessionId { get; set; }

    public bool Resume { get; set; }
}

public class ExecCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public ExecCommand(
        AgentRuntimeType agentType,
        AgwUserInput input)
    {
        AgentType = agentType;
        Input = input;
    }

    public required AgentRuntimeType AgentType { get; set; }

    public required AgwUserInput Input { get; set; }
}

public class InterruptCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
