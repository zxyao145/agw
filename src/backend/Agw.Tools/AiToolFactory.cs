using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;

namespace Agw.Domain.Tools;

/// <summary>
/// Factory for creating AIFunction instances from various sources.
/// Supports IAiTool implementations, attributed methods, and delegates.
/// </summary>
public class AiToolFactory
{
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiToolFactory"/> class.
    /// </summary>
    /// <param name="serviceProvider">Optional service provider for dependency injection.</param>
    public AiToolFactory(IServiceProvider? serviceProvider = null)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Creates an AIFunction from an IAiTool implementation.
    /// </summary>
    /// <param name="tool">The tool instance.</param>
    /// <returns>An AIFunction instance.</returns>
    public AIFunction CreateFromTool(IAiTool tool)
    {
        return tool.ToAIFunction();
    }

    /// <summary>
    /// Creates AIFunction instances from all IAiTool implementations in the provided collection.
    /// </summary>
    /// <param name="tools">The collection of tools.</param>
    /// <returns>A list of AIFunction instances.</returns>
    public IList<AIFunction> CreateFromTools(IEnumerable<IAiTool> tools)
    {
        return tools.Select(t => t.ToAIFunction()).ToList();
    }

    /// <summary>
    /// Creates an AIFunction from a static method marked with AiToolAttribute.
    /// </summary>
    /// <param name="method">The method info.</param>
    /// <returns>An AIFunction instance.</returns>
    public AIFunction CreateFromMethod(MethodInfo method)
    {
        if (!method.IsStatic)
        {
            throw new ArgumentException("Method must be static. For instance methods, use CreateFromMethod with a target.", nameof(method));
        }

        var attr = method.GetCustomAttribute<AiToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var name = attr?.Name ?? method.Name;

        var options = new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description
        };

        return AIFunctionFactory.Create(method, target: null, options);
    }

    /// <summary>
    /// Creates an AIFunction from an instance method marked with AiToolAttribute.
    /// </summary>
    /// <param name="method">The method info.</param>
    /// <param name="target">The target instance for the method.</param>
    /// <returns>An AIFunction instance.</returns>
    public AIFunction CreateFromMethod(MethodInfo method, object target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var attr = method.GetCustomAttribute<AiToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var name = attr?.Name ?? method.Name;

        var options = new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description
        };

        return AIFunctionFactory.Create(method, target, options);
    }

    /// <summary>
    /// Creates an AIFunction from an instance method with a factory for creating instances.
    /// Useful for methods on types that need dependency injection.
    /// </summary>
    /// <param name="method">The method info.</param>
    /// <param name="instanceFactory">Factory function to create instances.</param>
    /// <returns>An AIFunction instance.</returns>
    public AIFunction CreateFromMethod(MethodInfo method, Func<AIFunctionArguments, object> instanceFactory)
    {
        ArgumentNullException.ThrowIfNull(instanceFactory);

        var attr = method.GetCustomAttribute<AiToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var name = attr?.Name ?? method.Name;

        var options = new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description
        };

        return AIFunctionFactory.Create(method, instanceFactory, options);
    }

    /// <summary>
    /// Creates an AIFunction from a delegate.
    /// </summary>
    /// <param name="method">The delegate.</param>
    /// <param name="name">The name of the function.</param>
    /// <param name="description">The description of the function.</param>
    /// <returns>An AIFunction instance.</returns>
    public AIFunction CreateFromDelegate(Delegate method, string? name = null, string? description = null)
    {
        var options = new AIFunctionFactoryOptions
        {
            Name = name ?? method.Method.Name,
            Description = description
        };

        return AIFunctionFactory.Create(method, options);
    }

    /// <summary>
    /// Creates AIFunction instances from all attributed methods in a type.
    /// </summary>
    /// <param name="type">The type containing the methods.</param>
    /// <returns>A list of AIFunction instances.</returns>
    public IList<AIFunction> CreateFromType(Type type)
    {
        var functions = new List<AIFunction>();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null);

        foreach (var method in methods)
        {
            if (method.IsStatic)
            {
                functions.Add(CreateFromMethod(method));
            }
            else
            {
                // For instance methods, we need to create instances via DI or factory
                if (_serviceProvider != null)
                {
                    var instanceFactory = CreateInstanceFactory(type);
                    functions.Add(CreateFromMethod(method, instanceFactory));
                }
            }
        }

        return functions;
    }

    /// <summary>
    /// Creates AIFunction instances from all attributed methods in a type instance.
    /// </summary>
    /// <param name="instance">The instance containing the methods.</param>
    /// <returns>A list of AIFunction instances.</returns>
    public IList<AIFunction> CreateFromInstance(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var functions = new List<AIFunction>();
        var type = instance.GetType();
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null);

        foreach (var method in methods)
        {
            functions.Add(CreateFromMethod(method, instance));
        }

        // Also include static methods
        var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null);

        foreach (var method in staticMethods)
        {
            functions.Add(CreateFromMethod(method));
        }

        return functions;
    }

    /// <summary>
    /// Creates an instance factory for a type using the service provider.
    /// </summary>
    private Func<AIFunctionArguments, object> CreateInstanceFactory(Type type)
    {
        return (args) =>
        {
            if (_serviceProvider != null)
            {
                var instance = _serviceProvider.GetService(type);
                if (instance != null)
                {
                    return instance;
                }

                // Try to create using ActivatorUtilities
                return Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                    .CreateInstance(_serviceProvider, type);
            }

            // Fallback to parameterless constructor
            return Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Cannot create instance of {type.FullName}");
        };
    }

    /// <summary>
    /// Discovers and creates AIFunction instances from all tool classes in the specified assemblies.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>A list of AIFunction instances.</returns>
    public IList<AIFunction> DiscoverToolsFromAssemblies(params Assembly[] assemblies)
    {
        var functions = new List<AIFunction>();

        foreach (var assembly in assemblies)
        {
            var toolTypes = assembly.GetTypes()
                .Where(t => t.GetCustomAttribute<AiToolContainerAttribute>() != null
                    || t.Namespace?.StartsWith("Agw.Domain.Tools") == true);

            foreach (var type in toolTypes)
            {
                functions.AddRange(CreateFromType(type));
            }
        }

        return functions;
    }
}

/// <summary>
/// Extension methods for AiToolFactory.
/// </summary>
public static class AiToolFactoryExtensions
{
    /// <summary>
    /// Creates an AIFunction from a lambda expression.
    /// </summary>
    public static AIFunction Create(this AiToolFactory factory, Func<string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    /// <summary>
    /// Creates an AIFunction from a lambda expression with one parameter.
    /// </summary>
    public static AIFunction Create<T1>(this AiToolFactory factory, Func<T1, string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    /// <summary>
    /// Creates an AIFunction from a lambda expression with two parameters.
    /// </summary>
    public static AIFunction Create<T1, T2>(this AiToolFactory factory, Func<T1, T2, string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    /// <summary>
    /// Creates an AIFunction from a lambda expression with three parameters.
    /// </summary>
    public static AIFunction Create<T1, T2, T3>(this AiToolFactory factory, Func<T1, T2, T3, string> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    /// <summary>
    /// Creates an AIFunction from an async lambda expression.
    /// </summary>
    public static AIFunction CreateAsync(this AiToolFactory factory, Func<CancellationToken, Task<string>> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);

    /// <summary>
    /// Creates an AIFunction from an async lambda expression with one parameter.
    /// </summary>
    public static AIFunction CreateAsync<T1>(this AiToolFactory factory, Func<T1, CancellationToken, Task<string>> func, string name, string? description = null)
        => factory.CreateFromDelegate(func, name, description);
}
