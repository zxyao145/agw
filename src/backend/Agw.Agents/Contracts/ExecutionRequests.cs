using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Agents;

namespace Agw.Agents.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SettingCommand), nameof(SettingCommand))]
[JsonDerivedType(typeof(ExecCommand), nameof(ExecCommand))]
[JsonDerivedType(typeof(InterruptCommand), nameof(InterruptCommand))]
public abstract class AgentRunCommand;

public class SettingCommand : AgentRunCommand, IEquatable<SettingCommand>
{
    private Dictionary<string, string> _environmentVariables = new();

    [JsonConstructor]
    [SetsRequiredMembers]
    public SettingCommand(
        Guid projectId,
        Guid taskId,
        Dictionary<string, string>? environmentVariables = null,
        string? contextId = null)
    {
        ProjectId = projectId;
        TaskId = taskId;
        ContextId = contextId;
        EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>();
    }

    public Guid ProjectId { get; set; }

    public Guid TaskId { get; set; }

    public string? ContextId { get; set; }

    public Dictionary<string, string> EnvironmentVariables
    {
        get => _environmentVariables;
        set => _environmentVariables = value ?? new Dictionary<string, string>();
    }

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

        return left.ProjectId == right.ProjectId
            && left.TaskId == right.TaskId
            && string.Equals(left.ContextId, right.ContextId, StringComparison.Ordinal)
            && EnvironmentVariablesEqual(left.EnvironmentVariables, right.EnvironmentVariables);
    }

    public static bool operator !=(SettingCommand? left, SettingCommand? right) => !(left == right);

    public bool Equals(SettingCommand? other) => this == other;

    public override bool Equals(object? obj) => obj is SettingCommand other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            ProjectId,
            TaskId,
            ContextId,
            GetEnvironmentVariablesHashCode(EnvironmentVariables));

    private static bool EnvironmentVariablesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || rightValue != value)
            {
                return false;
            }
        }

        return true;
    }

    private static int GetEnvironmentVariablesHashCode(IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (environmentVariables == null || environmentVariables.Count == 0)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var (key, value) in environmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
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
