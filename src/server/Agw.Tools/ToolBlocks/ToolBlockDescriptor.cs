namespace Agw.Tools.ToolBlocks;

public sealed record ToolBlockDescriptor
{
    public ToolBlockDescriptor(
        string name,
        string displayName,
        string description,
        ToolBlockScope scopes,
        IReadOnlyList<ToolBlockMemberDescriptor> members,
        bool requiresWorkspace = false,
        bool excludeFromList = false,
        IReadOnlyList<string>? includedToolBlockNames = null
    )
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        Scopes = scopes;
        Members = members.ToArray();
        RequiresWorkspace = requiresWorkspace;
        ExcludeFromList = excludeFromList;
        IncludedToolBlockNames = (includedToolBlockNames ?? []).ToArray();
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public ToolBlockScope Scopes { get; }

    public IReadOnlyList<ToolBlockMemberDescriptor> Members { get; }

    public IReadOnlyList<string> MemberToolNames => Members.Select(static member => member.Name).ToArray();

    public bool RequiresWorkspace { get; }

    public bool ExcludeFromList { get; }

    /// <summary>
    /// Gets the Tool Blocks whose members this block also declares identically. When both are enabled, only this block
    /// is materialized, so each shared tool name is exposed once.
    /// 本 Block 以相同声明包含其全部成员的其他 Tool Block；同时启用时只物化本 Block，同名工具只暴露一次。
    /// </summary>
    public IReadOnlyList<string> IncludedToolBlockNames { get; }

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
