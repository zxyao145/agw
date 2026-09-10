using Agw.Shared.Exceptions;

namespace Agw.Tools.ToolBlocks;

public sealed class ToolBlockRegistry
{
    private readonly IReadOnlyDictionary<string, IToolBlock> _toolBlocks;
    private readonly IReadOnlyDictionary<string, string> _memberOwners;
    private readonly IReadOnlySet<string> _obsoleteToolBlockNames;

    public ToolBlockRegistry(IEnumerable<IToolBlock> toolBlocks)
    {
        var entries = new Dictionary<string, IToolBlock>(StringComparer.OrdinalIgnoreCase);
        var memberOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obsoleteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolBlock in toolBlocks)
        {
            ValidateDescriptor(toolBlock.Descriptor);
            if (IsObsolete(toolBlock.GetType()))
            {
                obsoleteNames.Add(toolBlock.Descriptor.Name);
                continue;
            }

            if (!entries.TryAdd(toolBlock.Descriptor.Name, toolBlock))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool Block '{toolBlock.Descriptor.Name}' is registered more than once."
                );
            }

            foreach (var memberToolName in toolBlock.Descriptor.MemberToolNames)
            {
                if (!memberOwners.TryAdd(memberToolName, toolBlock.Descriptor.Name))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool '{memberToolName}' belongs to more than one Tool Block."
                    );
                }
            }
        }

        foreach (var entry in entries)
        {
            if (memberOwners.TryGetValue(entry.Key, out var owner))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool Block '{entry.Key}' conflicts with member Tool '{entry.Key}' owned by Tool Block '{owner}'."
                );
            }
        }

        _toolBlocks = entries;
        _memberOwners = memberOwners;
        _obsoleteToolBlockNames = obsoleteNames
            .Except(entries.Keys, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlySet<string> ObsoleteToolBlockNames => _obsoleteToolBlockNames;

    public IReadOnlyList<ToolBlockDescriptor> GetDescriptors() =>
        _toolBlocks
            .Values.Select(static item => item.Descriptor)
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public ToolBlockDescriptor GetDescriptor(string name)
    {
        if (!_toolBlocks.TryGetValue(name, out var toolBlock))
        {
            if (_obsoleteToolBlockNames.Contains(name))
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"Tool Block '{name}' is obsolete and unavailable.");
            }

            throw new AgwException(ErrorCodes.InvalidParam, $"Unknown Tool Block '{name}'.");
        }

        return toolBlock.Descriptor;
    }

    public bool TryGetDescriptor(string name, out ToolBlockDescriptor descriptor)
    {
        if (_toolBlocks.TryGetValue(name, out var toolBlock))
        {
            descriptor = toolBlock.Descriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public bool TryGetMemberOwner(string toolName, out string toolBlockName) =>
        _memberOwners.TryGetValue(toolName, out toolBlockName!);

    public async ValueTask<ToolContribution> MaterializeAsync(
        IEnumerable<ToolBlockDefinition> definitions,
        ToolBlockScope scope,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        var result = new ToolContribution();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedDefinitions = new List<(ToolBlockDefinition, IToolBlock)>();
        var originalEnabledToolBlockNames = context.EnabledToolBlockNames;

        try
        {
            foreach (var definition in definitions)
            {
                var definitionName = definition.GetDefinitionName();
                if (string.IsNullOrWhiteSpace(definitionName) || !seen.Add(definitionName))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool Block name '{definitionName}' is empty or duplicated."
                    );
                }

                if (_obsoleteToolBlockNames.Contains(definitionName))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool Block '{definitionName}' is obsolete and unavailable."
                    );
                }

                if (!_toolBlocks.TryGetValue(definitionName, out var toolBlock))
                {
                    throw new AgwException(ErrorCodes.InvalidParam, $"Unknown Tool Block '{definitionName}'.");
                }

                if ((toolBlock.Descriptor.Scopes & scope) == 0)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool Block '{definitionName}' is not supported for scope '{scope}'."
                    );
                }

                resolvedDefinitions.Add((definition, toolBlock));
            }

            context.EnabledToolBlockNames = seen;

            foreach (var (definition, toolBlock) in resolvedDefinitions)
            {
                var contribution = await toolBlock
                    .MaterializeAsync(definition, context, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    var memberMetadata = toolBlock.Descriptor.Members.ToDictionary(
                        static member => member.Name,
                        member => new AgwToolMetadata(
                            $"tool-block:{toolBlock.Descriptor.Name}",
                            member.RequiredPermission,
                            member.AllowInPlanMode
                        ),
                        StringComparer.OrdinalIgnoreCase
                    );
                    var staticToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < contribution.Tools.Count; index++)
                    {
                        var tool = contribution.Tools[index];
                        if (!memberMetadata.TryGetValue(tool.Name, out var metadata))
                        {
                            throw new AgwException(
                                ErrorCodes.InvalidParam,
                                $"ToolBlock '{toolBlock.Descriptor.Name}' produced undeclared member '{tool.Name}'."
                            );
                        }
                        if (!staticToolNames.Add(tool.Name))
                        {
                            throw new AgwException(
                                ErrorCodes.InvalidParam,
                                $"ToolBlock member '{tool.Name}' was produced more than once."
                            );
                        }

                        contribution.Tools[index] = AgwToolMetadataBinding.Bind(tool, metadata);
                    }

                    foreach (var member in toolBlock.Descriptor.Members)
                    {
                        contribution.DynamicToolMetadata.Add(member.Name, memberMetadata[member.Name]);
                        if (member.AllowInPlanMode)
                        {
                            contribution.PlanModeAllowedToolNames.Add(member.Name);
                        }
                    }

                    var dynamicMetadata = memberMetadata
                        .Where(pair => !staticToolNames.Contains(pair.Key))
                        .ToDictionary(
                            static pair => pair.Key,
                            static pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase
                        );
                    if (contribution.ContextProviders.Count > 0)
                    {
                        var sourceProviders = contribution.ContextProviders.ToArray();
                        contribution.ContextProviders.Clear();
                        contribution.ContextProviders.Add(
                            new AgwToolMetadataContextProvider(sourceProviders, dynamicMetadata)
                        );
                    }
                    else if (dynamicMetadata.Count > 0)
                    {
                        throw new AgwException(
                            ErrorCodes.InvalidParam,
                            $"ToolBlock '{toolBlock.Descriptor.Name}' did not provide declared members: {string.Join(", ", dynamicMetadata.Keys.Order(StringComparer.Ordinal))}."
                        );
                    }
                    AddContribution(result, contribution);
                }
                catch
                {
                    await contribution.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            return result;
        }
        catch
        {
            context.EnabledToolBlockNames = originalEnabledToolBlockNames;
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddContribution(ToolContribution destination, ToolContribution contribution)
    {
        destination.Tools.AddRange(contribution.Tools);
        destination.PlanModeAllowedToolNames.UnionWith(contribution.PlanModeAllowedToolNames);
        destination.ContextProviders.AddRange(contribution.ContextProviders);
        destination.LoopEvaluators.AddRange(contribution.LoopEvaluators);
        destination.AutoApprovalRules.AddRange(contribution.AutoApprovalRules);
        foreach (var metadata in contribution.DynamicToolMetadata)
        {
            destination.DynamicToolMetadata.Add(metadata.Key, metadata.Value);
        }
        destination.Warnings.AddRange(contribution.Warnings);
        foreach (var warning in contribution.InvocationWarnings)
        {
            destination.InvocationWarnings[warning.Key] = warning.Value;
        }

        destination.AddResource(contribution);
    }

    private static bool IsObsolete(Type type) => type.IsDefined(typeof(ObsoleteAttribute), inherit: false);

    private static void ValidateDescriptor(ToolBlockDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "ToolBlock name is required.");
        }

        foreach (var member in descriptor.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Name) || !Enum.IsDefined(member.RequiredPermission))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"ToolBlock '{descriptor.Name}' has an invalid member declaration."
                );
            }
        }
    }
}
