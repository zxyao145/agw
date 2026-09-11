using Agw.Shared.Exceptions;
using Agw.Tools.Abstractions.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Generated;

public sealed class AgwGeneratedToolCatalog : IAgwGeneratedToolLookup
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlyDictionary<string, AgwGeneratedToolDescriptor> _byName;
    private readonly IReadOnlyDictionary<Type, IReadOnlyList<AgwGeneratedToolDescriptor>> _byType;

    public AgwGeneratedToolCatalog(IServiceScopeFactory scopeFactory, IEnumerable<IAgwGeneratedToolModule> modules)
    {
        _scopeFactory = scopeFactory;
        Modules = modules.ToArray();
        _byName = Modules
            .SelectMany(static module => module.Tools)
            .GroupBy(static tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group =>
                    group.Count() == 1
                        ? group.Single()
                        : throw new AgwException(
                            ErrorCodes.InvalidParam,
                            $"Generated Tool '{group.Key}' is registered more than once."
                        ),
                StringComparer.OrdinalIgnoreCase
            );
        _byType = Modules
            .SelectMany(static module => module.Tools)
            .GroupBy(static tool => tool.DeclaringType)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<AgwGeneratedToolDescriptor>)group.ToArray()
            );
    }

    public IReadOnlyList<IAgwGeneratedToolModule> Modules { get; }

    public IReadOnlyList<AgwGeneratedToolDescriptor> GetTools(Type declaringType) =>
        _byType.GetValueOrDefault(declaringType) ?? [];

    public AgwGeneratedToolDescriptor? GetTool(string name) => _byName.GetValueOrDefault(name);

    public AITool Create(AgwGeneratedToolDescriptor descriptor, Guid projectId) =>
        new GeneratedAgwFunction(descriptor, _scopeFactory, projectId);
}
