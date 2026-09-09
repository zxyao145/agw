using Agw.Shared.Exceptions;
using Agw.Tools.ToolBlocks;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Impl.ToolBlocks.UserMemory;

public sealed class UserMemoryToolBlock : IToolBlock
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public UserMemoryToolBlock(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.UserMemory,
            "User Memory",
            "Provides database-backed memory privately scoped to the current user across projects.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            [
                new(UserMemoryProvider.ListToolName, AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new(UserMemoryProvider.ReadToolName, AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new(UserMemoryProvider.WriteToolName, AgwToolPermission.Write),
                new(UserMemoryProvider.DeleteToolName, AgwToolPermission.Write),
            ]
        );

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        if (definition is not UserMemoryToolBlockDefinition { Options: not null })
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Block '{definition.GetDefinitionName()}' does not contain user memory options."
            );
        }
        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(
            Descriptor.Members.Where(static member => member.AllowInPlanMode).Select(static member => member.Name)
        );
        contribution.ContextProviders.Add(new UserMemoryProvider(_serviceScopeFactory));
        return ValueTask.FromResult(contribution);
    }
}
