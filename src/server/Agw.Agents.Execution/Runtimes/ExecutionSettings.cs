using System.Collections.ObjectModel;

namespace Agw.Agents.Execution.Runtimes;

public sealed class ExecutionSettings : IEquatable<ExecutionSettings>
{
    private readonly IReadOnlyDictionary<string, string> _environmentVariables;

    public ExecutionSettings(
        Guid projectId,
        Guid? conversationId = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        AgwPermissionMode? permissionMode = null,
        bool resume = false,
        HumanInteractionPolicy humanInteractionPolicy = HumanInteractionPolicy.Allow,
        long permissionVersion = 0,
        bool resultOnly = false
    )
    {
        ProjectId = projectId;
        ConversationId = conversationId;
        _environmentVariables = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environmentVariables ?? new Dictionary<string, string>())
        );
        PermissionMode = permissionMode;
        PermissionVersion = permissionVersion;
        Resume = resume;
        HumanInteractionPolicy = humanInteractionPolicy;
        ResultOnly = resultOnly;
    }

    public Guid ProjectId { get; }

    /// <summary>
    /// 设置所属的 Project Conversation；为空时由第一次受理的 Turn 确定。
    /// The Project Conversation the settings belong to; when empty, the first accepted turn decides it.
    /// </summary>
    public Guid? ConversationId { get; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables => _environmentVariables;

    public AgwPermissionMode? PermissionMode { get; }

    public long PermissionVersion { get; }

    public bool Resume { get; }

    public HumanInteractionPolicy HumanInteractionPolicy { get; }

    /// <summary>
    /// 只向客户端推送 result 消息，并拒绝本轮的人机交互请求。
    /// Streams only result messages to the client and declines human interaction for the turn.
    /// </summary>
    /// <remarks>
    /// 这是展示与交互开关，不参与相等判定，切换它不会重建 runtime。
    /// This is a presentation and interaction switch, excluded from equality so toggling it keeps the runtime.
    /// </remarks>
    public bool ResultOnly { get; }

    public static ExecutionSettings CreateDefault() => new(ProjectDefaults.DefaultBuiltInId);

    public ExecutionSettings WithPermissionMode(AgwPermissionMode permissionMode) =>
        new(
            ProjectId,
            ConversationId,
            _environmentVariables,
            permissionMode,
            Resume,
            HumanInteractionPolicy,
            PermissionMode == permissionMode ? PermissionVersion : checked(PermissionVersion + 1),
            ResultOnly
        );

    internal ExecutionSettings WithPermissionSnapshot(AgwPermissionMode? mode, long version) =>
        new(
            ProjectId,
            ConversationId,
            _environmentVariables,
            mode,
            Resume,
            HumanInteractionPolicy,
            version,
            ResultOnly
        );

    public ExecutionSettings WithHumanInteractionPolicy(HumanInteractionPolicy policy) =>
        new(
            ProjectId,
            ConversationId,
            _environmentVariables,
            PermissionMode,
            Resume,
            policy,
            PermissionVersion,
            ResultOnly
        );

    public ExecutionSettings WithResultOnly(bool resultOnly) =>
        new(
            ProjectId,
            ConversationId,
            _environmentVariables,
            PermissionMode,
            Resume,
            HumanInteractionPolicy,
            PermissionVersion,
            resultOnly
        );

    internal ExecutionSettings WithConversationId(Guid conversationId) =>
        new(
            ProjectId,
            conversationId,
            _environmentVariables,
            PermissionMode,
            Resume,
            HumanInteractionPolicy,
            PermissionVersion,
            ResultOnly
        );

    public bool Equals(ExecutionSettings? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other != null
            && ProjectId == other.ProjectId
            && ConversationId == other.ConversationId
            && PermissionMode == other.PermissionMode
            && Resume == other.Resume
            && HumanInteractionPolicy == other.HumanInteractionPolicy
            && EnvironmentVariablesEqual(_environmentVariables, other._environmentVariables);
    }

    public override bool Equals(object? obj) => obj is ExecutionSettings other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProjectId);
        hash.Add(ConversationId);
        hash.Add(PermissionMode);
        hash.Add(Resume);
        hash.Add(HumanInteractionPolicy);
        foreach (var (key, value) in _environmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

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
