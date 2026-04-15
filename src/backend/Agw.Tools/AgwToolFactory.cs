using System.Reflection;

using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Domain.Tools;

/// <summary>
/// Factory for creating <see cref="AITool"/> instances from tool implementations,
/// attributed methods, and delegates.
/// </summary>
public class AgwToolFactory
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
    /// Creates an <see cref="AITool"/> from an <see cref="IAgwTool"/> implementation.
    /// </summary>
    public AITool CreateFromTool(IAgwTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.ToAITool();
    }

    /// <summary>
    /// Creates <see cref="AITool"/> instances from all provided tool implementations.
    /// </summary>
    public IList<AITool> CreateFromTools(IEnumerable<IAgwTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return tools.Select(CreateFromTool).ToList();
    }

    /// <summary>
    /// Creates an <see cref="AITool"/> from a static method marked with <see cref="AiToolAttribute"/>.
    /// </summary>
    public AITool CreateFromMethod(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (!method.IsStatic)
        {
            throw new AgwException(ErrorCodes.MethodMustBeStatic, "Method must be static. For instance methods, use CreateFromMethod with a target.");
        }

        return AIFunctionFactory.Create(method, target: null, CreateOptions(method));
    }

    /// <summary>
    /// Creates an <see cref="AITool"/> from an instance method marked with <see cref="AiToolAttribute"/>.
    /// </summary>
    public AITool CreateFromMethod(MethodInfo method, object target)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(target);

        return AIFunctionFactory.Create(method, target, CreateOptions(method));
    }

    /// <summary>
    /// Creates an <see cref="AITool"/> from an instance method with a factory for creating instances.
    /// </summary>
    public AITool CreateFromMethod(MethodInfo method, Func<AIFunctionArguments, object> instanceFactory)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(instanceFactory);

        return AIFunctionFactory.Create(method, instanceFactory, CreateOptions(method));
    }

    /// <summary>
    /// Creates an <see cref="AITool"/> from a delegate.
    /// </summary>
    public AITool CreateFromDelegate(Delegate method, string? name = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        var options = new AIFunctionFactoryOptions
        {
            Name = name ?? method.Method.Name,
            Description = description
        };

        return AIFunctionFactory.Create(method, options);
    }

    /// <summary>
    /// Creates <see cref="AITool"/> instances from the supported tool declarations on a type.
    /// </summary>
    public IList<AITool> CreateFromType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var tools = new List<AITool>();

        if (!type.IsAbstract && !type.IsInterface && typeof(IAgwTool).IsAssignableFrom(type))
        {
            tools.Add(CreateFromTool(CreateToolInstance(type)));
        }

        foreach (var method in GetAttributedMethods(type))
        {
            if (method.IsStatic)
            {
                tools.Add(CreateFromMethod(method));
                continue;
            }

            if (_serviceProvider != null)
            {
                tools.Add(CreateFromMethod(method, CreateInstanceFactory(type)));
                continue;
            }

            var target = Activator.CreateInstance(type);
            if (target != null)
            {
                tools.Add(CreateFromMethod(method, target));
            }
        }

        return tools;
    }

    /// <summary>
    /// Creates <see cref="AITool"/> instances from an object instance.
    /// </summary>
    public IList<AITool> CreateFromInstance(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var tools = new List<AITool>();

        if (instance is IAgwTool agwTool)
        {
            tools.Add(CreateFromTool(agwTool));
        }

        var type = instance.GetType();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null))
        {
            tools.Add(CreateFromMethod(method, instance));
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null))
        {
            tools.Add(CreateFromMethod(method));
        }

        return tools;
    }

    /// <summary>
    /// Discovers and creates tools from the specified assemblies.
    /// </summary>
    public IList<AITool> DiscoverToolsFromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var tools = new List<AITool>();
        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().Where(IsToolType))
            {
                foreach (var tool in CreateFromType(type))
                {
                    if (registeredNames.Add(tool.Name))
                    {
                        tools.Add(tool);
                    }
                }
            }
        }

        return tools;
    }

    private static IEnumerable<MethodInfo> GetAttributedMethods(Type type)
    {
        return type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null);
    }

    private static bool IsToolType(Type type)
    {
        return type.GetCustomAttribute<AiToolContainerAttribute>() != null
            || GetAttributedMethods(type).Any()
            || (!type.IsAbstract && !type.IsInterface && typeof(IAgwTool).IsAssignableFrom(type));
    }

    private static AIFunctionFactoryOptions CreateOptions(MethodInfo method)
    {
        var attr = method.GetCustomAttribute<AiToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var name = attr?.Name ?? method.Name;

        return new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description
        };
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
                ?? throw new AgwException(ErrorCodes.CannotCreateInstance, $"Cannot create instance of {type.FullName}");
        };
    }

    private IAgwTool CreateToolInstance(Type type)
    {
        object instance;
        if (_serviceProvider != null)
        {
            instance = ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, type);
        }
        else
        {
            instance = Activator.CreateInstance(type)
                ?? throw new AgwException(ErrorCodes.CannotCreateInstance, $"Cannot create instance of {type.FullName}");
        }

        return (IAgwTool)instance;
    }
}

/// <summary>
/// Extension methods for <see cref="AgwToolFactory"/>.
/// </summary>
public static class AiToolFactoryExtensions
{
    public static AITool Create(this AgwToolFactory factory, Func<string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    public static AITool Create<T1>(this AgwToolFactory factory, Func<T1, string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    public static AITool Create<T1, T2>(this AgwToolFactory factory, Func<T1, T2, string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    public static AITool Create<T1, T2, T3>(this AgwToolFactory factory, Func<T1, T2, T3, string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    public static AITool CreateAsync(this AgwToolFactory factory, Func<CancellationToken, Task<string>> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    public static AITool CreateAsync<T1>(this AgwToolFactory factory, Func<T1, CancellationToken, Task<string>> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);
}
