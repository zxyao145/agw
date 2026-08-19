using Agw.Shared.Exceptions;

namespace Agw.Tools;

public sealed class ToolValueResolutionResult
{
    public ToolValueResolutionResult(IReadOnlyList<ToolDefinition> tools, IReadOnlyList<ToolBlockDefinition> toolBlocks)
    {
        Tools = tools;
        ToolBlocks = toolBlocks;
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public IReadOnlyList<ToolBlockDefinition> ToolBlocks { get; }
}

public static class ToolValueResolution
{
    public static ToolValueResolutionResult Resolve(
        IReadOnlyList<ToolValueObject>? agentValues,
        IReadOnlyList<ToolValueObject>? projectValues
    )
    {
        var agent = ValidateUnique(agentValues ?? [], "Agent");
        var project = ValidateUnique(projectValues ?? [], "Project");
        if (project.Any(static value => value is ToolBlockValue { Definition: BackgroundAgentsToolBlockDefinition }))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Block '{ToolBlockDefinitionNames.BackgroundAgents}' is only supported by Agent definitions."
            );
        }

        var result = agent.ToList();
        var indexes = result
            .Select(static (value, index) => (value.GetDefinitionName(), index))
            .ToDictionary(static item => item.Item1, static item => item.index, StringComparer.OrdinalIgnoreCase);

        foreach (var value in project)
        {
            var name = value.GetDefinitionName();
            if (indexes.TryGetValue(name, out var index))
            {
                result[index] = value;
                continue;
            }

            indexes.Add(name, result.Count);
            result.Add(value);
        }

        return new ToolValueResolutionResult(
            result.OfType<ToolValue>().Select(static value => value.Definition).ToArray(),
            result.OfType<ToolBlockValue>().Select(static value => value.Definition).ToArray()
        );
    }

    private static IReadOnlyList<ToolValueObject> ValidateUnique(IReadOnlyList<ToolValueObject> values, string owner)
    {
        var validationError = ToolValueObjectValidation.GetError(values);
        if (validationError != null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"{owner}: {validationError}");
        }

        return values;
    }
}
