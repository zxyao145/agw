using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Setting;

public class SettingCommand : AgentRunCommand, IEquatable<SettingCommand>
{
    private Dictionary<string, string> _environmentVariables = new();

    [JsonConstructor]
    [SetsRequiredMembers]
    public SettingCommand(
        Guid projectId,
        Dictionary<string, string>? environmentVariables = null,
        string? contextId = null,
        AgwPermissionMode? permissionMode = null,
        bool resultOnly = false
    )
    {
        ProjectId = projectId;
        ContextId = contextId;
        EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>();
        PermissionMode = permissionMode;
        ResultOnly = resultOnly;
    }

    public Guid ProjectId { get; set; }

    public string? ContextId { get; set; }

    public Dictionary<string, string> EnvironmentVariables
    {
        get => _environmentVariables;
        set => _environmentVariables = value ?? new Dictionary<string, string>();
    }

    public AgwPermissionMode? PermissionMode { get; set; }

    /// <summary>
    /// 只向客户端推送 result 消息，并拒绝本轮的人机交互请求。
    /// Streams only result messages to the client and declines human interaction for the turn.
    /// </summary>
    public bool ResultOnly { get; set; }

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
            && string.Equals(left.ContextId, right.ContextId, StringComparison.Ordinal)
            && left.PermissionMode == right.PermissionMode
            && left.ResultOnly == right.ResultOnly
            && EnvironmentVariablesEqual(left.EnvironmentVariables, right.EnvironmentVariables);
    }

    public static bool operator !=(SettingCommand? left, SettingCommand? right) => !(left == right);

    public bool Equals(SettingCommand? other) => this == other;

    public override bool Equals(object? obj) => obj is SettingCommand other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            ProjectId,
            ContextId,
            PermissionMode,
            ResultOnly,
            GetEnvironmentVariablesHashCode(EnvironmentVariables)
        );

    private static bool EnvironmentVariablesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right
    )
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
