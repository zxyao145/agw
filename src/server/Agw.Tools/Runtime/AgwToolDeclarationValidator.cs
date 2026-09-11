using System.Reflection;
using Agw.Shared.Exceptions;

namespace Agw.Tools.Runtime;

/// <summary>Validates declarations independently of how their tools are exposed.</summary>
public static class AgwToolDeclarationValidator
{
    public static void Validate(IAgwToolMeta tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ValidateStatelessType(tool.GetType());
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Tool '{tool.GetType().FullName}' has no name.");
        }
        if (string.IsNullOrWhiteSpace(tool.Category))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Tool '{tool.Name}' has no category.");
        }
        if (!Enum.IsDefined(tool.RequiredPermission))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool '{tool.Name}' declares invalid permission '{tool.RequiredPermission}'."
            );
        }
    }

    public static void ValidateStatelessType(Type type)
    {
        var mutableFields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            )
            .Where(static field => !field.IsInitOnly && !field.IsLiteral)
            .Select(static field => field.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (mutableFields.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool type '{type.FullName}' must be stateless; mutable fields: {string.Join(", ", mutableFields)}."
            );
        }
    }
}
