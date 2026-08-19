using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.ToolBlocks.Blocks.UserMemory;

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
                UserMemoryProvider.ListToolName,
                UserMemoryProvider.ReadToolName,
                UserMemoryProvider.WriteToolName,
                UserMemoryProvider.DeleteToolName,
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
        contribution.PlanModeAllowedToolNames.UnionWith([
            UserMemoryProvider.ListToolName,
            UserMemoryProvider.ReadToolName,
        ]);
        contribution.ContextProviders.Add(new UserMemoryProvider(_serviceScopeFactory));
        return ValueTask.FromResult(contribution);
    }
}
