namespace Agw.Tools.ToolBlocks;

[Flags]
public enum ToolBlockScope
{
    None = 0,
    Agent = 1,
    Project = 2,
}

public sealed record ToolBlockDescriptor
{
    public ToolBlockDescriptor(
        string name,
        string displayName,
        string description,
        ToolBlockScope scopes,
        IReadOnlyList<ToolBlockMemberDescriptor> members,
        bool requiresWorkspace = false
    )
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        Scopes = scopes;
        Members = members.ToArray();
        RequiresWorkspace = requiresWorkspace;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public ToolBlockScope Scopes { get; }

    public IReadOnlyList<ToolBlockMemberDescriptor> Members { get; }

    public IReadOnlyList<string> MemberToolNames => Members.Select(static member => member.Name).ToArray();

    public bool RequiresWorkspace { get; }

    public bool MayRequireApproval =>
        Members.Any(static member => AgwToolMetadataBinding.RequiresApproval(member.RequiredPermission));
}

public sealed record ToolBlockMemberDescriptor : IAgwToolMeta
{
    public ToolBlockMemberDescriptor(
        string name,
        AgwToolPermission requiredPermission,
        bool allowInPlanMode = false,
        string category = "Tool Blocks"
    )
    {
        Name = name;
        RequiredPermission = requiredPermission;
        AllowInPlanMode = allowInPlanMode;
        Category = category;
    }

    public string Name { get; }

    public string Category { get; }

    public bool AllowInPlanMode { get; }

    public AgwToolPermission RequiredPermission { get; }
}
