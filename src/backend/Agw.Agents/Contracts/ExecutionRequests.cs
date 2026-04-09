using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Agw.Shared.Models;

namespace Agw.Api.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SettingCommand), nameof(SettingCommand))]
[JsonDerivedType(typeof(ExecCommand), nameof(ExecCommand))]
[JsonDerivedType(typeof(InterruptCommand), nameof(InterruptCommand))]
public abstract class AgentRunCommand;

public class SettingCommand : AgentRunCommand, IEquatable<SettingCommand>
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public SettingCommand(
        Guid projectId,
        Guid taskId,
        string? workspace,
        string settingContent = "{}")
    {
        ProjectId = projectId;
        TaskId = taskId;
        Workspace = workspace;
        SettingContent = settingContent;
    }

    public Guid ProjectId { get; set; }

    public Guid TaskId { get; set; }

    public string? Workspace { get; set; }

    public string SettingContent { get; set; }

    [JsonIgnore]
    public bool Resume { get; set; }

    public static bool operator ==(SettingCommand? left, SettingCommand? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.SettingContent == right.SettingContent
            && left.ProjectId == right.ProjectId
            && left.TaskId == right.TaskId;
    }

    public static bool operator !=(SettingCommand? left, SettingCommand? right) => !(left == right);

    public bool Equals(SettingCommand? other) => this == other;

    public override bool Equals(object? obj) => obj is SettingCommand other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(SettingContent, ProjectId, TaskId);
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

    public AgentRuntimeType AgentType { get; set; }

    public AgwUserInput Input { get; set; }
}

public class InterruptCommand : AgentRunCommand
{
    public string? Reason { get; set; }
}
