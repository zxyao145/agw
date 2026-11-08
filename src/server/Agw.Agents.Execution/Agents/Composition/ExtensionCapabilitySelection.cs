namespace Agw.Agents.Execution.Agents.Composition;

public sealed record ExtensionToolSelection(Guid ExtensionId, string ToolId);

public sealed record ExtensionCapabilitySelection(
    IReadOnlyList<ExtensionToolSelection> Tools,
    IReadOnlyList<Guid> SkillExtensionIds
)
{
    public static ExtensionCapabilitySelection Empty { get; } = new([], []);

    public IReadOnlyList<Guid> ExtensionIds =>
        Tools
            .Select(selection => selection.ExtensionId)
            .Concat(SkillExtensionIds)
            .Where(extensionId => extensionId != Guid.Empty)
            .Distinct()
            .Order()
            .ToArray();
}
