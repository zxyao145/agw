using System.Collections.ObjectModel;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Shared.Contracts.Projects;

namespace Agw.Agents.Execution.Connections;

public sealed class ExecutionSettings : IEquatable<ExecutionSettings>
{
    private readonly IReadOnlyDictionary<string, string> _environmentVariables;

    private ExecutionSettings(
        Guid projectId,
        string? contextId,
        IReadOnlyDictionary<string, string> environmentVariables,
        PermissionMode? permissionMode,
        bool resume
    )
    {
        ProjectId = projectId;
        ContextId = contextId;
        _environmentVariables = environmentVariables;
        PermissionMode = permissionMode;
        Resume = resume;
    }

    public Guid ProjectId { get; }

    public string? ContextId { get; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environmentVariables;

    public PermissionMode? PermissionMode { get; }

    public bool Resume { get; }

    public static ExecutionSettings FromCommand(SettingCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new ExecutionSettings(
            command.ProjectId,
            command.ContextId,
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(command.EnvironmentVariables)),
            command.PermissionMode,
            command.Resume
        );
    }

    public static ExecutionSettings CreateDefault() =>
        FromCommand(new SettingCommand(ProjectDefaults.DefaultBuiltInId));

    public ExecutionSettings WithPermissionMode(PermissionMode permissionMode) =>
        new(ProjectId, ContextId, _environmentVariables, permissionMode, Resume);

    public bool Equals(ExecutionSettings? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other != null
            && ProjectId == other.ProjectId
            && string.Equals(ContextId, other.ContextId, StringComparison.Ordinal)
            && PermissionMode == other.PermissionMode
            && Resume == other.Resume
            && EnvironmentVariablesEqual(_environmentVariables, other._environmentVariables);
    }

    public override bool Equals(object? obj) => obj is ExecutionSettings other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProjectId);
        hash.Add(ContextId, StringComparer.Ordinal);
        hash.Add(PermissionMode);
        hash.Add(Resume);
        foreach (var (key, value) in _environmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    internal SettingCommand ToCommand() =>
        new(ProjectId, new Dictionary<string, string>(_environmentVariables), ContextId, PermissionMode)
        {
            Resume = Resume,
        };

    private static bool EnvironmentVariablesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (
                !right.TryGetValue(key, out var rightValue)
                || !string.Equals(value, rightValue, StringComparison.Ordinal)
            )
            {
                return false;
            }
        }

        return true;
    }
}
