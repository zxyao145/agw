using System.Reflection;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Infrastructure;

/// <summary>
/// Factory for creating <see cref="AITool"/> instances from tool implementations,
/// attributed methods, and delegates.
/// </summary>
internal sealed class AgwToolFactory
{
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgwToolFactory"/> class.
    /// </summary>
    /// <param name="serviceProvider">Optional service provider for dependency injection.</param>
    public AgwToolFactory(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates an <see cref="AITool"/> from a static method marked with <see cref="AiToolAttribute"/>.
    /// </summary>
    public AITool CreateFromMethod(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (method.IsStatic)
        {
            return AIFunctionFactory.Create(method, target: null, CreateOptions(method));
        }

        var declaringType =
            method.DeclaringType
            ?? throw new AgwException(ErrorCodes.CannotCreateInstance, "Attributed Tool method has no declaring type.");
        return AIFunctionFactory.Create(method, CreateInstanceFactory(declaringType), CreateOptions(method));
    }

    private static AIFunctionFactoryOptions CreateOptions(MethodInfo method)
    {
        var attr = method.GetCustomAttribute<AiToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var name = attr?.Name ?? method.Name;

        return new AIFunctionFactoryOptions { Name = name, Description = description };
    }

    private Func<AIFunctionArguments, object> CreateInstanceFactory(Type type)
    {
        return _ =>
        {
            if (_serviceProvider != null)
            {
                return ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, type);
            }

            return Activator.CreateInstance(type)
                ?? throw new AgwException(
                    ErrorCodes.CannotCreateInstance,
                    $"Cannot create instance of {type.FullName}"
                );
        };
    }
}
