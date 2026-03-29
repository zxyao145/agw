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
    public required string SettingContent { get; set; }
}

public class ExecCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public ExecCommand(
        AgentRuntimeType agentType,
        AgwUserInput input,
        Guid projectId,
        Guid taskId,
        string? sessionId = null)
    {
        AgentType = agentType;
        Input = input;
        ProjectId = projectId;
        TaskId = taskId;
        if (string.IsNullOrEmpty(sessionId))
        {
            SessionId = TaskId.ToString();
        }
    }

    public required AgentRuntimeType AgentType { get; set; }

    public required AgwUserInput Input { get; set; }

    public required Guid ProjectId { get; set; }

    public required Guid TaskId { get; set; }

    public string? SessionId { get; set; }
}

public class InterruptCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
