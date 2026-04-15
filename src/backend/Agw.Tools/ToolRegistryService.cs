using System.Reflection;

using Agw.Domain.Tools;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;
using Agw.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Domain.Services;

/// <summary>
/// Service for discovering, registering, and managing AI tools available to agents.
/// Supports both attribute-based methods and <see cref="IAgwTool"/> implementations.
/// </summary>
public class ToolRegistryService
{
    private readonly Dictionary<string, MethodInfo> _methods = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolInfo> _toolInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IAgwTool> _toolInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly AgwToolFactory _toolFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<ToolRegistryService> _logger;

    public ToolRegistryService(ILogger<ToolRegistryService> logger, IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _toolFactory = new AgwToolFactory(serviceProvider);
        DiscoverTools();
    }

    /// <summary>
    /// Discovers all tools available in the current assembly.
    /// </summary>
    private void DiscoverTools()
    {
        var assemblies = AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Agw.") ?? false)
            .ToList();

        foreach (var asm in assemblies)
        {
            _logger.LogInformation("Discovering tools in assembly: {AssemblyName}", asm.FullName);

            DiscoverAttributedMethods(asm);
            DiscoverToolImplementations(asm);
        }
    }

    /// <summary>
    /// Discovers public static methods marked with <see cref="AiToolAttribute"/>.
    /// </summary>
    private void DiscoverAttributedMethods(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            var containerAttr = type.GetCustomAttribute<AiToolContainerAttribute>();
            var defaultCategory = containerAttr?.DefaultCategory ?? "General";

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null))
            {
                RegisterMethod(method, defaultCategory);
            }
        }
    }

    /// <summary>
    /// Discovers and instantiates <see cref="IAgwTool"/> implementations.
    /// </summary>
    private void DiscoverToolImplementations(Assembly assembly)
    {
        var toolTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IAgwTool).IsAssignableFrom(t));

        foreach (var type in toolTypes)
        {
            try
            {
                RegisterTool(CreateToolInstance(type));
            }
            catch (Exception)
            {
                // Ignore tool types that require unavailable dependencies.
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
        _toolInfos[methodName] = BuildMethodToolInfo(method, defaultCategory);
    }

    /// <summary>
    /// Registers an <see cref="IAgwTool"/> instance.
    /// </summary>
    public void RegisterTool(IAgwTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        _toolInstances[tool.Name] = tool;
        _toolInfos[tool.Name] = BuildRegisteredToolInfo(tool);
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
    /// Gets the <see cref="MethodInfo"/> for a tool backed by an attributed method.
    /// </summary>
    public MethodInfo? GetToolMethod(string name)
    {
        return _methods.TryGetValue(name, out var method) ? method : null;
    }

    /// <summary>
    /// Gets a registered <see cref="IAgwTool"/> instance by its name.
    /// </summary>
    public IAgwTool? GetToolInstance(string name)
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
    /// Creates an <see cref="AITool"/> for a tool by its name.
    /// </summary>
    public AITool? CreateAIFunction(string name)
    {
        if (_toolInstances.TryGetValue(name, out var tool))
        {
            return tool.ToAITool();
        }

        if (_methods.TryGetValue(name, out var method))
        {
            return _toolFactory.CreateFromMethod(method);
        }

        return null;
    }

    /// <summary>
    /// Creates <see cref="AITool"/> instances for a list of tool names.
    /// </summary>
    public IList<AITool> CreateAIFunctions(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var tools = new List<AITool>();
        foreach (var name in names)
        {
            var tool = CreateAIFunction(name);
            if (tool != null)
            {
                tools.Add(tool);
            }
        }

        return tools;
    }

    /// <summary>
    /// Creates <see cref="AITool"/> instances for all registered tools.
    /// </summary>
    public IList<AITool> CreateAllAIFunctions()
    {
        return CreateAIFunctions(_toolInfos.Keys);
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

    private static ToolInfo BuildMethodToolInfo(MethodInfo method, string defaultCategory)
    {
        var toolAttr = method.GetCustomAttribute<AiToolAttribute>()!;
        var category = !string.IsNullOrWhiteSpace(toolAttr.Category) && toolAttr.Category != "General"
            ? toolAttr.Category
            : defaultCategory;

        return new ToolInfo
        {
            Name = toolAttr.Name ?? method.Name,
            Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            Category = category,
            TypeName = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "Unknown",
            Parameters = BuildParameters(method.GetParameters()),
            IsAsync = IsAsyncReturnType(method.ReturnType),
            RequiresConfirmation = toolAttr.RequiresConfirmation,
            TimeoutMs = toolAttr.TimeoutMs
        };
    }

    private static ToolInfo BuildRegisteredToolInfo(IAgwTool tool)
    {
        var toolType = tool.GetType();
        var executeMethod = ResolveExecuteMethod(toolType);
        var aiTool = tool.ToAITool();

        return new ToolInfo
        {
            Name = tool.Name,
            Description = ResolveDescription(tool, executeMethod, aiTool),
            Category = ResolveCategory(tool),
            TypeName = toolType.FullName ?? toolType.Name,
            Parameters = executeMethod == null ? [] : BuildParameters(executeMethod.GetParameters()),
            IsAsync = executeMethod != null && IsAsyncReturnType(executeMethod.ReturnType),
            RequiresConfirmation = ResolveRequiresConfirmation(tool, aiTool),
            TimeoutMs = ResolveTimeoutMs(tool)
        };
    }

    private static MethodInfo? ResolveExecuteMethod(Type toolType)
    {
        return toolType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)
            ?? toolType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
    }

    private static string ResolveDescription(IAgwTool tool, MethodInfo? executeMethod, AITool aiTool)
    {
        var toolType = tool.GetType();
        var descriptionProperty = toolType.GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
        if (descriptionProperty?.PropertyType == typeof(string)
            && descriptionProperty.GetValue(tool) is string description
            && !string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        return executeMethod?.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? aiTool.Description
            ?? string.Empty;
    }

    private static string ResolveCategory(IAgwTool tool)
    {
        var toolType = tool.GetType();
        var categoryProperty = toolType.GetProperty("Category", BindingFlags.Public | BindingFlags.Instance);
        if (categoryProperty?.PropertyType == typeof(string)
            && categoryProperty.GetValue(tool) is string category
            && !string.IsNullOrWhiteSpace(category))
        {
            return category;
        }

        return "General";
    }

    private static bool ResolveRequiresConfirmation(IAgwTool tool, AITool aiTool)
    {
        if (string.Equals(
                aiTool.GetType().FullName,
                "Microsoft.Extensions.AI.ApprovalRequiredAIFunction",
                StringComparison.Ordinal))
        {
            return true;
        }

        var toolType = tool.GetType();
        var approvalProperty = toolType.GetProperty("ApprovalRequired", BindingFlags.Public | BindingFlags.Instance);
        if (approvalProperty?.PropertyType == typeof(bool)
            && approvalProperty.GetValue(tool) is bool approvalRequired)
        {
            return approvalRequired;
        }

        return false;
    }

    private static int ResolveTimeoutMs(IAgwTool tool)
    {
        var toolType = tool.GetType();
        var timeoutProperty = toolType.GetProperty("TimeoutMs", BindingFlags.Public | BindingFlags.Instance);
        if (timeoutProperty?.PropertyType == typeof(int)
            && timeoutProperty.GetValue(tool) is int timeoutMs
            && timeoutMs > 0)
        {
            return timeoutMs;
        }

        return 30000;
    }

    private static IReadOnlyList<ToolParameterInfo> BuildParameters(IEnumerable<ParameterInfo> parameters)
    {
        return parameters.Select(p => new ToolParameterInfo
        {
            Name = p.Name ?? "param",
            Type = GetFriendlyTypeName(p.ParameterType),
            Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description,
            IsOptional = p.IsOptional || p.HasDefaultValue,
            DefaultValue = p.HasDefaultValue ? p.DefaultValue : null,
            SchemaType = p.GetCustomAttribute<AiToolParameterSchemaAttribute>()?.Type,
            Format = p.GetCustomAttribute<AiToolParameterSchemaAttribute>()?.Format,
            EnumValues = ParseEnumValues(p.GetCustomAttribute<AiToolParameterSchemaAttribute>()?.EnumValues)
        }).ToList();
    }

    private static IReadOnlyList<string>? ParseEnumValues(string? enumValues)
    {
        if (string.IsNullOrWhiteSpace(enumValues))
        {
            return null;
        }

        return enumValues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsAsyncReturnType(Type returnType)
    {
        return typeof(Task).IsAssignableFrom(returnType)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            || returnType == typeof(ValueTask);
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
