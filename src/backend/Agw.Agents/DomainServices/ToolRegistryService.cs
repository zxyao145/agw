using Agw.Domain.Tools;
using Agw.Shared.Models;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;

namespace Agw.Domain.Services;

/// <summary>
/// Service for discovering, registering, and managing AI tools available to agents.
/// Supports both attribute-based static methods and IAiTool implementations.
/// </summary>
public class ToolRegistryService
{
    private readonly Dictionary<string, MethodInfo> _methods = new();
    private readonly Dictionary<string, ToolInfo> _toolInfos = new();
    private readonly Dictionary<string, IAiTool> _toolInstances = new();
    private readonly AiToolFactory _toolFactory;

    public ToolRegistryService(IServiceProvider? serviceProvider = null)
    {
        _toolFactory = new AiToolFactory(serviceProvider);
        //DiscoverTools();
    }

    /// <summary>
    /// Discovers all methods marked with AiToolAttribute and IAiTool implementations
    /// in the Agw.Domain.Tools namespace.
    /// </summary>
    private void DiscoverTools()
    {
        var assembly = typeof(AiToolAttribute).Assembly;

        // Discover attributed static methods
        DiscoverAttributedMethods(assembly);

        // Discover IAiTool implementations
        DiscoverToolImplementations(assembly);
    }

    /// <summary>
    /// Discovers methods marked with AiToolAttribute.
    /// </summary>
    private void DiscoverAttributedMethods(Assembly assembly)
    {
        var toolTypes = assembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("Agw."));

        foreach (var type in toolTypes)
        {
            var containerAttr = type.GetCustomAttribute<AiToolContainerAttribute>();
            var defaultCategory = containerAttr?.DefaultCategory ?? "General";

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null);

            foreach (var method in methods)
            {
                RegisterMethod(method, defaultCategory);
            }
        }
    }

    /// <summary>
    /// Discovers and instantiates IAiTool implementations.
    /// </summary>
    private void DiscoverToolImplementations(Assembly assembly)
    {
        var toolTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                && !t.IsInterface
                && typeof(IAiTool).IsAssignableFrom(t)
                && t.Namespace?.StartsWith("Agw.Domain.Tools") == true);

        foreach (var type in toolTypes)
        {
            try
            {
                // Try to create instance (supports parameterless constructors)
                if (Activator.CreateInstance(type) is IAiTool tool)
                {
                    RegisterTool(tool);
                }
            }
            catch (Exception)
            {
                // Skip types that can't be instantiated without DI
                // These should be registered via DI in Program.cs
            }
        }
    }

    /// <summary>
    /// Registers a method as a tool.
    /// </summary>
    private void RegisterMethod(MethodInfo method, string defaultCategory)
    {
        var toolAttr = method.GetCustomAttribute<AiToolAttribute>()!;
        var methodName = toolAttr.Name ?? method.Name;

        _methods[methodName] = method;

        var parameters = method.GetParameters()
            .Select(p => new ToolParameterInfo
            {
                Name = p.Name ?? "param",
                Type = GetFriendlyTypeName(p.ParameterType),
                Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                IsOptional = p.IsOptional || p.HasDefaultValue
            })
            .ToList();

        var category = !string.IsNullOrEmpty(toolAttr.Category) && toolAttr.Category != "General"
            ? toolAttr.Category
            : defaultCategory;

        _toolInfos[methodName] = new ToolInfo
        {
            Name = methodName,
            Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            Category = category,
            TypeName = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "Unknown",
            Parameters = parameters,
            IsAsync = false,
            RequiresConfirmation = toolAttr.RequiresConfirmation,
            TimeoutMs = toolAttr.TimeoutMs
        };
    }

    /// <summary>
    /// Registers an IAiTool instance.
    /// </summary>
    public void RegisterTool(IAiTool tool)
    {
        _toolInstances[tool.Name] = tool;

        // Create ToolInfo for the registered tool
        if (!_toolInfos.ContainsKey(tool.Name))
        {
            _toolInfos[tool.Name] = new ToolInfo
            {
                Name = tool.Name,
                Description = tool.Description,
                Category = tool.Category,
                TypeName = tool.GetType().FullName ?? tool.GetType().Name,
                Parameters = [],
                IsAsync = tool is IAsyncAiTool,
                RequiresConfirmation = false,
                TimeoutMs = 30000
            };
        }
    }

    /// <summary>
    /// Gets all available tools.
    /// </summary>
    public IReadOnlyList<ToolInfo> GetAllTools()
    {
        return _toolInfos.Values.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }

    /// <summary>
    /// Gets a tool by its name.
    /// </summary>
    public ToolInfo? GetTool(string name)
    {
        return _toolInfos.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>
    /// Gets the MethodInfo for a tool by its name.
    /// </summary>
    public MethodInfo? GetToolMethod(string name)
    {
        return _methods.TryGetValue(name, out var method) ? method : null;
    }

    /// <summary>
    /// Gets an IAiTool instance by its name.
    /// </summary>
    public IAiTool? GetToolInstance(string name)
    {
        return _toolInstances.TryGetValue(name, out var tool) ? tool : null;
    }

    /// <summary>
    /// Checks if a tool exists.
    /// </summary>
    public bool ToolExists(string name)
    {
        return _methods.ContainsKey(name) || _toolInstances.ContainsKey(name);
    }

    /// <summary>
    /// Gets all tools by category.
    /// </summary>
    public IReadOnlyDictionary<string, List<ToolInfo>> GetToolsByCategory()
    {
        return _toolInfos.Values
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
    }

    /// <summary>
    /// Creates an AIFunction for a tool by its name.
    /// </summary>
    public AIFunction? CreateAIFunction(string name)
    {
        // First check for IAiTool instances
        if (_toolInstances.TryGetValue(name, out var tool))
        {
            return tool.ToAIFunction();
        }

        // Then check for static methods
        if (_methods.TryGetValue(name, out var method))
        {
            return _toolFactory.CreateFromMethod(method);
        }

        return null;
    }

    /// <summary>
    /// Creates AIFunction instances for a list of tool names.
    /// </summary>
    public IList<AIFunction> CreateAIFunctions(IEnumerable<string> names)
    {
        var functions = new List<AIFunction>();

        foreach (var name in names)
        {
            var function = CreateAIFunction(name);
            if (function != null)
            {
                functions.Add(function);
            }
        }

        return functions;
    }

    /// <summary>
    /// Creates AIFunction instances for all registered tools.
    /// </summary>
    public IList<AIFunction> CreateAllAIFunctions()
    {
        return CreateAIFunctions(_toolInfos.Keys);
    }

    /// <summary>
    /// Gets a friendly type name for display purposes.
    /// </summary>
    private static string GetFriendlyTypeName(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(DateTimeOffset)) return "DateTimeOffset";
        if (type == typeof(Guid)) return "Guid";

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(Nullable<>))
            {
                return GetFriendlyTypeName(type.GetGenericArguments()[0]) + "?";
            }

            var genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
            var baseName = type.Name.Split('`')[0];
            return $"{baseName}<{genericArgs}>";
        }

        return type.Name;
    }
}
