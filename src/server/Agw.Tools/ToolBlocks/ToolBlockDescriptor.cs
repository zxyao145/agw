namespace Agw.Tools.ToolBlocks;

[Flags]
public enum ToolBlockScope
{
    None = 0,
    Agent = 1,
    Project = 2
}

public sealed record ToolBlockDescriptor
{
    public ToolBlockDescriptor(
        string name,
        string displayName,
        string description,
        ToolBlockScope scopes,
        IReadOnlyList<string> memberToolNames,
        bool requiresWorkspace = false,
        bool mayRequireApproval = false)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        Scopes = scopes;
        MemberToolNames = memberToolNames;
        RequiresWorkspace = requiresWorkspace;
        MayRequireApproval = mayRequireApproval;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public ToolBlockScope Scopes { get; }

    public IReadOnlyList<string> MemberToolNames { get; }

    public bool RequiresWorkspace { get; }

    public bool MayRequireApproval { get; }
}
