using Agw.Shared.Exceptions;

namespace Agw.Tools.ToolBlocks;

public sealed class ToolBlockRegistry
{
    private readonly IReadOnlyDictionary<string, IToolBlock> _toolBlocks;
    private readonly IReadOnlyDictionary<string, string> _memberOwners;

    public ToolBlockRegistry(IEnumerable<IToolBlock> toolBlocks)
    {
        var entries = new Dictionary<string, IToolBlock>(StringComparer.OrdinalIgnoreCase);
        var memberOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolBlock in toolBlocks)
        {
            if (!entries.TryAdd(toolBlock.Descriptor.Name, toolBlock))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool Block '{toolBlock.Descriptor.Name}' is registered more than once.");
            }

            foreach (var memberToolName in toolBlock.Descriptor.MemberToolNames)
            {
                if (!memberOwners.TryAdd(memberToolName, toolBlock.Descriptor.Name))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool '{memberToolName}' belongs to more than one Tool Block.");
                }
            }
        }

        foreach (var entry in entries)
        {
            if (memberOwners.TryGetValue(entry.Key, out var owner))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool Block '{entry.Key}' conflicts with member Tool '{entry.Key}' owned by Tool Block '{owner}'.");
            }
        }

        _toolBlocks = entries;
        _memberOwners = memberOwners;
    }

    public IReadOnlyList<ToolBlockDescriptor> GetDescriptors() =>
        _toolBlocks.Values
            .Select(static item => item.Descriptor)
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public ToolBlockDescriptor GetDescriptor(string name)
    {
        if (!_toolBlocks.TryGetValue(name, out var toolBlock))
        {
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
        CancellationToken cancellationToken)
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
                if (string.IsNullOrWhiteSpace(definitionName) ||
                    !seen.Add(definitionName))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool Block name '{definitionName}' is empty or duplicated.");
                }

                if (!_toolBlocks.TryGetValue(definitionName, out var toolBlock))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Unknown Tool Block '{definitionName}'.");
                }

                if ((toolBlock.Descriptor.Scopes & scope) == 0)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool Block '{definitionName}' is not supported for scope '{scope}'.");
                }

                resolvedDefinitions.Add((definition, toolBlock));
            }

            context.EnabledToolBlockNames = seen;

            foreach (var (definition, toolBlock) in resolvedDefinitions)
            {
                var contribution = await toolBlock
                    .MaterializeAsync(definition, context, cancellationToken)
                    .ConfigureAwait(false);
                AddContribution(result, contribution);
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

    private static void AddContribution(
        ToolContribution destination,
        ToolContribution contribution)
    {
        destination.Tools.AddRange(contribution.Tools);
        destination.ContextProviders.AddRange(contribution.ContextProviders);
        destination.LoopEvaluators.AddRange(contribution.LoopEvaluators);
        destination.AutoApprovalRules.AddRange(contribution.AutoApprovalRules);
        destination.Warnings.AddRange(contribution.Warnings);
        foreach (var warning in contribution.InvocationWarnings)
        {
            destination.InvocationWarnings[warning.Key] = warning.Value;
        }

        destination.AddResource(contribution);
    }
}
