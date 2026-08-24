using System.Reflection;
using Agw.Shared.Contracts.Tools;
using Agw.Shared.Contracts.Tools.Abstractions;
using Agw.Shared.Exceptions;
using Agw.Tools.ContextualTools;
using Agw.Tools.HumanInteraction;
using Agw.Tools.ToolBlocks;
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
    private readonly HashSet<string> _obsoleteToolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, IContextualTool> _contextualTools;
    private readonly ToolBlockRegistry _toolBlockRegistry;
    private readonly AgwToolFactory _toolFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<ToolRegistryService> _logger;

    public ToolRegistryService(
        ILogger<ToolRegistryService> logger,
        IServiceProvider serviceProvider,
        IEnumerable<IContextualTool>? contextualTools = null,
        ToolBlockRegistry? toolBlockRegistry = null
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _toolFactory = new AgwToolFactory(serviceProvider);
        _toolBlockRegistry = toolBlockRegistry ?? new ToolBlockRegistry([]);
        var resolvedContextualTools = (contextualTools ?? []).ToArray();
        _contextualTools = BuildContextualToolCatalog(
            resolvedContextualTools.Where(static tool => !IsObsolete(tool.GetType()))
        );
        foreach (var tool in resolvedContextualTools.Where(static tool => IsObsolete(tool.GetType())))
        {
            if (!_contextualTools.ContainsKey(tool.Descriptor.Name))
            {
                _obsoleteToolNames.Add(tool.Descriptor.Name);
            }
        }

        DiscoverTools();
        ValidateCatalog();
    }

    /// <summary>
    /// Discovers all tools available in the current assembly.
    /// </summary>
    private void DiscoverTools()
    {
        var assemblies = AppDomain
            .CurrentDomain.GetAssemblies()
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

            foreach (
                var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.GetCustomAttribute<AiToolAttribute>() != null)
            )
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
        var toolTypes = assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IAgwTool).IsAssignableFrom(t));

        foreach (var type in toolTypes)
        {
            try
            {
                RegisterTool(CreateToolInstance(type));
            }
            catch (AgwException)
            {
                throw;
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
        if (IsObsolete(method) || method.DeclaringType is { } declaringType && IsObsolete(declaringType))
        {
            RecordObsoleteTool(methodName, method);
            return;
        }

        EnsureIndependentToolNameAvailable(methodName);

        _obsoleteToolNames.Remove(methodName);
        _methods[methodName] = method;
        _toolInfos[methodName] = BuildMethodToolInfo(method, defaultCategory);
    }

    /// <summary>
    /// Registers an <see cref="IAgwTool"/> instance.
    /// </summary>
    public void RegisterTool(IAgwTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (IsObsolete(tool.GetType()))
        {
            RecordObsoleteTool(tool.Name, tool.GetType());
            return;
        }

        EnsureIndependentToolNameAvailable(tool.Name);

        _obsoleteToolNames.Remove(tool.Name);
        _toolInstances[tool.Name] = tool;
        _toolInfos[tool.Name] = BuildRegisteredToolInfo(tool);
    }

    /// <summary>
    /// Gets all available tools.
    /// </summary>
    public IReadOnlyList<ToolInfo> GetAllTools()
    {
        return _toolInfos
            .Values.Concat(_contextualTools.Values.Select(static tool => tool.Descriptor))
            .Concat(_toolBlockRegistry.GetDescriptors().Select(ToToolInfo))
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Name)
            .ToList();
    }

    /// <summary>
    /// Gets a tool by its name.
    /// </summary>
    public ToolInfo? GetTool(string name)
    {
        if (_toolInfos.TryGetValue(name, out var tool))
        {
            return tool;
        }

        if (_contextualTools.TryGetValue(name, out var contextualTool))
        {
            return contextualTool.Descriptor;
        }

        return _toolBlockRegistry
            .GetDescriptors()
            .Where(descriptor => string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(ToToolInfo)
            .SingleOrDefault();
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
        return _methods.ContainsKey(name) || _toolInstances.ContainsKey(name) || _contextualTools.ContainsKey(name);
    }

    /// <summary>
    /// Gets all tools by category.
    /// </summary>
    public IReadOnlyDictionary<string, List<ToolInfo>> GetToolsByCategory()
    {
        return GetAllTools().GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
    }

    public async ValueTask<ToolContribution> MaterializeAsync(
        IEnumerable<ToolDefinition> definitions,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(context);

        var result = new ToolContribution();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var definition in definitions)
            {
                var name = definition?.GetDefinitionName() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                {
                    throw new AgwException(ErrorCodes.InvalidParam, $"Tool name '{name}' is empty or duplicated.");
                }

                if (_obsoleteToolNames.Contains(name))
                {
                    throw new AgwException(ErrorCodes.InvalidParam, $"Tool '{name}' is obsolete and unavailable.");
                }

                if (_toolBlockRegistry.TryGetMemberOwner(name, out var owner))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool '{name}' belongs to Tool Block '{owner}' and cannot be selected independently."
                    );
                }

                if (_toolBlockRegistry.TryGetDescriptor(name, out _))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"'{name}' is a Tool Block and must use kind '{ToolValueObjectKinds.ToolBlock}'."
                    );
                }

                if (_contextualTools.TryGetValue(name, out var contextualTool))
                {
                    var contribution = await contextualTool
                        .MaterializeAsync(definition!, context, cancellationToken)
                        .ConfigureAwait(false);
                    AddContribution(result, contribution);
                    continue;
                }

                var tool = CreateAIFunction(name, context.ProjectId);
                if (tool == null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, $"Unknown Tool '{name}'.");
                }

                // durable Activity 不能在进程内等待用户，因此复用 MAF approval 边界把调用交还 orchestration。
                var materializedTool =
                    context.DeferHumanInteractions && tool is HumanInteractionRequiredAIFunction interaction
                        ? new ApprovalRequiredAIFunction(interaction)
                        : tool;
                result.Tools.Add(materializedTool);
                if (IsAllowedInPlanMode(name))
                {
                    result.PlanModeAllowedToolNames.Add(materializedTool.Name);
                }
            }

            return result;
        }
        catch
        {
            await result.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
    /// Creates an <see cref="AITool"/> for a tool by its name, binding it to a specific project.
    /// </summary>
    public AITool? CreateAIFunction(string name, Guid projectId)
    {
        if (_toolInstances.TryGetValue(name, out var tool))
        {
            if (tool is IProjectScopedAgwTool projectScoped)
            {
                return projectScoped.ToAITool(projectId);
            }

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
    /// Creates <see cref="AITool"/> instances for a list of tool names, binding project-scoped tools to a specific project.
    /// </summary>
    public IList<AITool> CreateAIFunctions(IEnumerable<string> names, Guid projectId)
    {
        ArgumentNullException.ThrowIfNull(names);

        var tools = new List<AITool>();
        foreach (var name in names)
        {
            var tool = CreateAIFunction(name, projectId);
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
            instance =
                Activator.CreateInstance(type)
                ?? throw new AgwException(
                    ErrorCodes.CannotCreateInstance,
                    $"Cannot create instance of {type.FullName}"
                );
        }

        return (IAgwTool)instance;
    }

    private static ToolInfo BuildMethodToolInfo(MethodInfo method, string defaultCategory)
    {
        var toolAttr = method.GetCustomAttribute<AiToolAttribute>()!;
        var category =
            !string.IsNullOrWhiteSpace(toolAttr.Category) && toolAttr.Category != "General"
                ? toolAttr.Category
                : defaultCategory;

        return new ToolInfo
        {
            Name = toolAttr.Name ?? method.Name,
            DisplayName = toolAttr.Name ?? method.Name,
            Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            Category = category,
            TypeName = method.DeclaringType?.FullName ?? method.DeclaringType?.Name ?? "Unknown",
            Parameters = BuildParameters(method.GetParameters()),
            IsAsync = IsAsyncReturnType(method.ReturnType),
            RequiresConfirmation = toolAttr.RequiresConfirmation,
            TimeoutMs = toolAttr.TimeoutMs,
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
            DisplayName = tool.Name,
            Description = ResolveDescription(tool, executeMethod, aiTool),
            Category = ResolveCategory(tool),
            TypeName = toolType.FullName ?? toolType.Name,
            Parameters = executeMethod == null ? [] : BuildParameters(executeMethod.GetParameters()),
            IsAsync = executeMethod != null && IsAsyncReturnType(executeMethod.ReturnType),
            RequiresConfirmation = ResolveRequiresConfirmation(tool, aiTool),
            TimeoutMs = ResolveTimeoutMs(tool),
        };
    }

    private void ValidateCatalog()
    {
        foreach (var contextualTool in _contextualTools.Values)
        {
            if (_toolInfos.ContainsKey(contextualTool.Descriptor.Name))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool '{contextualTool.Descriptor.Name}' is registered more than once."
                );
            }
        }

        foreach (var toolBlock in _toolBlockRegistry.GetDescriptors())
        {
            if (_toolInfos.ContainsKey(toolBlock.Name) || _contextualTools.ContainsKey(toolBlock.Name))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool Block '{toolBlock.Name}' conflicts with a Tool of the same name."
                );
            }

            foreach (var memberToolName in toolBlock.MemberToolNames)
            {
                if (_toolInfos.ContainsKey(memberToolName) || _contextualTools.ContainsKey(memberToolName))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidParam,
                        $"Tool Block '{toolBlock.Name}' member '{memberToolName}' conflicts with an independently registered Tool."
                    );
                }
            }
        }

        var executableToolNames = _toolInfos
            .Keys.Concat(_contextualTools.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definedToolNames = ToolDefinitionNames.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingDefinitions = executableToolNames
            .Except(definedToolNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingDefinitions.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Selectable Tools are missing ToolDefinition types: {string.Join(", ", missingDefinitions)}."
            );
        }
    }

    public void ValidateDefinitionCoverage()
    {
        var executableToolNames = _toolInfos
            .Keys.Concat(_contextualTools.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingImplementations = ToolDefinitionNames
            .All.Except(executableToolNames, StringComparer.OrdinalIgnoreCase)
            .Except(_obsoleteToolNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingImplementations.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"ToolDefinition types are missing executable Tools: {string.Join(", ", missingImplementations)}."
            );
        }

        var registeredToolBlockNames = _toolBlockRegistry
            .GetDescriptors()
            .Select(static descriptor => descriptor.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingToolBlockImplementations = ToolBlockDefinitionNames
            .All.Except(registeredToolBlockNames, StringComparer.OrdinalIgnoreCase)
            .Except(_toolBlockRegistry.ObsoleteToolBlockNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingToolBlockImplementations.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"ToolBlockDefinition types are missing Tool Blocks: {string.Join(", ", missingToolBlockImplementations)}."
            );
        }

        var missingToolBlockDefinitions = registeredToolBlockNames
            .Except(ToolBlockDefinitionNames.All, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingToolBlockDefinitions.Length > 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Blocks are missing ToolBlockDefinition types: {string.Join(", ", missingToolBlockDefinitions)}."
            );
        }
    }

    private static IReadOnlyDictionary<string, IContextualTool> BuildContextualToolCatalog(
        IEnumerable<IContextualTool> tools
    )
    {
        var result = new Dictionary<string, IContextualTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!result.TryAdd(tool.Descriptor.Name, tool))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Contextual Tool '{tool.Descriptor.Name}' is registered more than once."
                );
            }
        }

        return result;
    }

    private void RecordObsoleteTool(string name, MemberInfo member)
    {
        if (!_toolInfos.ContainsKey(name) && !_contextualTools.ContainsKey(name))
        {
            _obsoleteToolNames.Add(name);
        }

        _logger.LogDebug(
            "Skipping obsolete Tool {ToolName} implemented by {ToolMember}.",
            name,
            member.DeclaringType?.FullName ?? member.Name
        );
    }

    private static bool IsObsolete(MemberInfo member) => member.IsDefined(typeof(ObsoleteAttribute), inherit: false);

    private void EnsureIndependentToolNameAvailable(string name)
    {
        if (!ToolDefinitionNames.All.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool '{name}' does not have a registered ToolDefinition."
            );
        }

        if (_toolInfos.ContainsKey(name))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Tool '{name}' is registered more than once.");
        }
    }

    private static ToolInfo ToToolInfo(ToolBlockDescriptor descriptor) =>
        new()
        {
            Kind = ToolCatalogItemKind.ToolBlock,
            Name = descriptor.Name,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            Category = "Tool Blocks",
            TypeName = string.Empty,
            Parameters = [],
            MemberToolNames = descriptor.MemberToolNames,
            Scopes = (ToolScope)(int)descriptor.Scopes,
            RequiresWorkspace = descriptor.RequiresWorkspace,
            RequiresConfirmation = descriptor.MayRequireApproval,
        };

    private static void AddContribution(ToolContribution destination, ToolContribution contribution)
    {
        destination.Tools.AddRange(contribution.Tools);
        destination.PlanModeAllowedToolNames.UnionWith(contribution.PlanModeAllowedToolNames);
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

    private bool IsAllowedInPlanMode(string name)
    {
        if (_toolInstances.TryGetValue(name, out var tool))
        {
            return tool.AllowInPlanMode;
        }

        return _methods.TryGetValue(name, out var method)
            && method.GetCustomAttribute<AiToolAttribute>()?.AllowInPlanMode == true;
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
        if (
            descriptionProperty?.PropertyType == typeof(string)
            && descriptionProperty.GetValue(tool) is string description
            && !string.IsNullOrWhiteSpace(description)
        )
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
        if (
            categoryProperty?.PropertyType == typeof(string)
            && categoryProperty.GetValue(tool) is string category
            && !string.IsNullOrWhiteSpace(category)
        )
        {
            return category;
        }

        return "General";
    }

    private static bool ResolveRequiresConfirmation(IAgwTool tool, AITool aiTool)
    {
        if (
            string.Equals(
                aiTool.GetType().FullName,
                "Microsoft.Extensions.AI.ApprovalRequiredAIFunction",
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        var toolType = tool.GetType();
        var approvalProperty = toolType.GetProperty("ApprovalRequired", BindingFlags.Public | BindingFlags.Instance);
        if (approvalProperty?.PropertyType == typeof(bool) && approvalProperty.GetValue(tool) is bool approvalRequired)
        {
            return approvalRequired;
        }

        return false;
    }

    private static int ResolveTimeoutMs(IAgwTool tool)
    {
        var toolType = tool.GetType();
        var timeoutProperty = toolType.GetProperty("TimeoutMs", BindingFlags.Public | BindingFlags.Instance);
        if (
            timeoutProperty?.PropertyType == typeof(int)
            && timeoutProperty.GetValue(tool) is int timeoutMs
            && timeoutMs > 0
        )
        {
            return timeoutMs;
        }

        return 30000;
    }

    private static IReadOnlyList<ToolParameterInfo> BuildParameters(IEnumerable<ParameterInfo> parameters)
    {
        return parameters
            .Select(p => new ToolParameterInfo
            {
                Name = p.Name ?? "param",
                Type = GetFriendlyTypeName(p.ParameterType),
                Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                IsOptional = p.IsOptional || p.HasDefaultValue,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null,
                SchemaType = p.GetCustomAttribute<AiToolParameterSchemaAttribute>()?.Type,
                Format = p.GetCustomAttribute<AiToolParameterSchemaAttribute>()?.Format,
                EnumValues = ParseEnumValues(p.GetCustomAttribute<AiToolParameterSchemaAttribute>()?.EnumValues),
            })
            .ToList();
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
        if (type == typeof(string))
            return "string";
        if (type == typeof(int))
            return "int";
        if (type == typeof(long))
            return "long";
        if (type == typeof(double))
            return "double";
        if (type == typeof(float))
            return "float";
        if (type == typeof(decimal))
            return "decimal";
        if (type == typeof(bool))
            return "bool";
        if (type == typeof(DateTimeOffset))
            return "DateTimeOffset";
        if (type == typeof(Guid))
            return "Guid";

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
