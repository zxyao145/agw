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
        Guid? taskId = null,
        string? sessionId = null)
    {
        AgentType = agentType;
        Input = input;
        ProjectId = projectId;
        TaskId = taskId;
        if (string.IsNullOrEmpty(sessionId) && TaskId.HasValue)
        {
            SessionId = TaskId.Value.ToString();
        }
        else
        {
            SessionId = sessionId;
        }
    }

    public required AgentRuntimeType AgentType { get; set; }

    public required AgwUserInput Input { get; set; }

    public required Guid ProjectId { get; set; }

    public Guid? TaskId { get; set; }

    public string? SessionId { get; set; }
}

public class InterruptCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
