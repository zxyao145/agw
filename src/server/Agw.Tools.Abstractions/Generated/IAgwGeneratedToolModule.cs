using Agw.Tools.Abstractions.ToolBlocks;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Abstractions.Generated;

public interface IAgwGeneratedToolModule
{
    IReadOnlyList<AgwGeneratedToolDescriptor> Tools { get; }

    IReadOnlyList<IAgwGeneratedToolBlockRegistration> ToolBlockRegistrations { get; }

    IReadOnlyList<string> ObsoleteToolNames { get; }
}

public sealed record AgwGeneratedToolDescriptor(
    Type DeclaringType,
    string Name,
    string DisplayName,
    string Description,
    string Category,
    AgwToolPermission RequiredPermission,
    bool AllowInPlanMode,
    bool ExcludeFromList,
    bool RequiresWorkspace,
    int TimeoutMs,
    bool IsAsync,
    string JsonSchema,
    string? ReturnJsonSchema,
    IReadOnlyList<AgwGeneratedToolParameterDescriptor> Parameters,
    Func<IAgwToolBindingContext, CancellationToken, ValueTask<object?>> InvokeAsync
);

public sealed record AgwGeneratedToolParameterDescriptor(
    string Name,
    string Type,
    string? Description,
    bool IsOptional,
    object? DefaultValue,
    string? SchemaType,
    string? Format,
    IReadOnlyList<string>? EnumValues
);

public sealed record AgwGeneratedToolBlockDescriptor(
    string Name,
    string DisplayName,
    string Description,
    ToolBlockScope Scopes,
    bool ExcludeFromList,
    IReadOnlyList<string> MemberToolNames
);

public interface IAgwToolSet<TToolContainer>
    where TToolContainer : class;

public interface IAgwToolBlockDeclaration
{
    string Name { get; }

    string DisplayName => Name;

    string Description => string.Empty;

    ToolBlockScope Scopes => ToolBlockScope.Agent | ToolBlockScope.Project;

    bool ExcludeFromList => false;
}

public interface IAgwGeneratedToolBlockRegistration : IAgwToolBlockDeclaration
{
    IReadOnlyList<Type> ToolTypes { get; }

    AgwGeneratedToolBlockDescriptor CreateDescriptor(IAgwGeneratedToolLookup lookup);
}

public interface IAgwGeneratedToolLookup
{
    IReadOnlyList<AgwGeneratedToolDescriptor> GetTools(Type declaringType);
}

public interface IAgwToolBindingContext
{
    Guid ProjectId { get; }

    IServiceProvider Services { get; }

    AIFunctionArguments Arguments { get; }

    T GetRequiredArgument<T>(string name);

    T GetOptionalArgument<T>(string name, T defaultValue);

    T GetRequiredService<T>()
        where T : notnull;
}

public interface IAgwToolInvocationContext
{
    Guid ProjectId { get; }
}

public interface IAgwToolInvocationContextInitializer : IAgwToolInvocationContext
{
    void Initialize(Guid projectId);
}
