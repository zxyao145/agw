using System.Reflection;
using Agw.Shared.Exceptions;
using Agw.Tools.Abstractions.Generated;
using Agw.Tools.Generated;
using Agw.Tools.ToolBlocks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Tools;

/// <summary>
/// Service for discovering, registering, and managing AI tools available to agents.
/// Supports both attribute-based methods and <see cref="IAgwTool"/> implementations.
/// </summary>
public sealed class ToolRegistryService
{
    private readonly Dictionary<string, ToolInfo> _toolInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Type> _toolTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _obsoleteToolNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, IContextualTool> _contextualTools;
    private readonly ToolBlockRegistry _toolBlockRegistry;
    private readonly AgwGeneratedToolCatalog _generatedToolCatalog;
    private readonly Dictionary<string, AgwGeneratedToolDescriptor> _generatedTools = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ToolRegistryService> _logger;

    public ToolRegistryService(
        ILogger<ToolRegistryService> logger,
        IServiceProvider serviceProvider,
        IEnumerable<IContextualTool>? contextualTools = null,
        ToolBlockRegistry? toolBlockRegistry = null,
        bool discoverAllToolKinds = false,
        IEnumerable<Assembly>? toolAssemblies = null,
        AgwGeneratedToolCatalog? generatedToolCatalog = null,
        IEnumerable<Type>? generatedToolTypes = null
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _generatedToolCatalog =
            generatedToolCatalog
            ?? new AgwGeneratedToolCatalog(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                [Agw.Generated.Agw.Tools.AgwToolModule.Instance]
            );
        var assemblies = (toolAssemblies ?? GetToolAssemblies())
            .DistinctBy(static assembly => assembly.FullName, StringComparer.Ordinal)
            .ToArray();
        var selectedGeneratedTypes = (generatedToolTypes ?? []).ToHashSet();
        var resolvedContextualTools = (
            contextualTools ?? (discoverAllToolKinds ? DiscoverInstances<IContextualTool>(assemblies) : [])
        ).ToArray();
        foreach (var tool in resolvedContextualTools)
        {
            AgwToolDeclarationValidator.Validate(tool);
        }
        _contextualTools = BuildContextualToolCatalog(
            resolvedContextualTools.Where(static tool => !IsObsolete(tool.GetType()))
        );
        foreach (var tool in resolvedContextualTools.Where(static tool => IsObsolete(tool.GetType())))
        {
            if (!_contextualTools.ContainsKey(tool.Name))
            {
                _obsoleteToolNames.Add(tool.Name);
            }
        }

        var generatedBlocks = _generatedToolCatalog
            .Modules.SelectMany(static module => module.ToolBlockRegistrations)
            .Select(registration => registration.CreateDescriptor(_generatedToolCatalog))
            .Where(block =>
                block.MemberToolNames.Any(name =>
                    _generatedToolCatalog.GetTool(name) is { } member
                    && (
                        assemblies.Contains(member.DeclaringType.Assembly)
                        || selectedGeneratedTypes.Contains(member.DeclaringType)
                    )
                )
            )
            .Select(block => (IToolBlock)new GeneratedToolBlock(block, _generatedToolCatalog));
        _toolBlockRegistry =
            toolBlockRegistry
            ?? new ToolBlockRegistry(
                (discoverAllToolKinds ? DiscoverInstances<IToolBlock>(assemblies) : []).Concat(generatedBlocks)
            );
        DiscoverTools(assemblies, selectedGeneratedTypes);
        ValidateCatalog();
    }

    public ToolBlockRegistry ToolBlocks => _toolBlockRegistry;

    /// <summary>
    /// Discovers all tools available in the current assembly.
    /// </summary>
    private void DiscoverTools(IReadOnlyList<Assembly> assemblies, IReadOnlySet<Type> selectedGeneratedTypes)
    {
        foreach (var asm in assemblies)
        {
            _logger.LogInformation("Discovering tools in assembly: {AssemblyName}", asm.FullName);

            DiscoverToolImplementations(asm);
        }

        DiscoverGeneratedTools(assemblies, selectedGeneratedTypes);
    }

    private void DiscoverGeneratedTools(IReadOnlyList<Assembly> assemblies, IReadOnlySet<Type> selectedGeneratedTypes)
    {
        foreach (IAgwGeneratedToolModule module in _generatedToolCatalog.Modules)
        {
            bool ownsSelectedAssembly = module.Tools.Any(tool =>
                assemblies.Contains(tool.DeclaringType.Assembly) || selectedGeneratedTypes.Contains(tool.DeclaringType)
            );
            if (!ownsSelectedAssembly)
            {
                continue;
            }
            foreach (string obsoleteName in module.ObsoleteToolNames)
            {
                _obsoleteToolNames.Add(obsoleteName);
            }
            foreach (
                AgwGeneratedToolDescriptor tool in module.Tools.Where(tool =>
                    (
                        assemblies.Contains(tool.DeclaringType.Assembly)
                        || selectedGeneratedTypes.Contains(tool.DeclaringType)
                    ) && !_toolBlockRegistry.TryGetMemberOwner(tool.Name, out _)
                )
            )
            {
                EnsureIndependentToolNameAvailable(tool.Name);
                _generatedTools.Add(tool.Name, tool);
                _toolInfos.Add(tool.Name, BuildGeneratedToolInfo(tool));
            }
        }
    }

    /// <summary>
    /// Discovers and instantiates <see cref="IAgwTool"/> implementations.
    /// </summary>
    private void DiscoverToolImplementations(Assembly assembly)
    {
        var toolTypes = GetLoadableTypes(assembly)
            .Where(t =>
                !t.IsAbstract
                && !t.IsInterface
                && (typeof(IAgwTool).IsAssignableFrom(t) || typeof(IProjectScopedAgwTool).IsAssignableFrom(t))
            );

        foreach (var type in toolTypes.Where(static type => !type.ContainsGenericParameters))
        {
            RegisterToolDefinition(CreateToolInstance(type));
        }
    }

    /// <summary>
    /// Registers an <see cref="IAgwTool"/> instance.
    /// </summary>
    public void RegisterTool(IAgwTool tool) => RegisterToolDefinition(tool);

    public void RegisterTool(IProjectScopedAgwTool tool) => RegisterToolDefinition(tool);

    private void RegisterToolDefinition(IAgwToolMeta tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        AgwToolDeclarationValidator.Validate(tool);
        if (IsObsolete(tool.GetType()))
        {
            RecordObsoleteTool(tool.Name, tool.GetType());
            return;
        }

        EnsureIndependentToolNameAvailable(tool.Name);

        _obsoleteToolNames.Remove(tool.Name);
        _toolTypes[tool.Name] = tool.GetType();
        _toolInfos[tool.Name] = BuildRegisteredToolInfo(tool);
    }

    /// <summary>
    /// Gets all available tools.
    /// </summary>
    public IReadOnlyList<ToolInfo> GetAllTools()
    {
        return _toolInfos
            .Values.Concat(_contextualTools.Values.Select(BuildMetaToolInfo))
            .Concat(_toolBlockRegistry.GetDescriptors().Select(ToToolInfo))
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Name)
            .ToList();
    }

    public IReadOnlyList<ToolInfo> GetListedTools() =>
        GetAllTools().Where(static tool => !tool.ExcludeFromList).ToArray();

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
            return BuildMetaToolInfo(contextualTool);
        }

        return _toolBlockRegistry
            .GetDescriptors()
            .Where(descriptor => string.Equals(descriptor.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(ToToolInfo)
            .SingleOrDefault();
    }

    /// <summary>
    /// Gets an ordinary or project-scoped Tool declaration by its name.
    /// </summary>
    public IAgwToolMeta? GetToolInstance(string name)
    {
        return _toolTypes.TryGetValue(name, out var type) ? CreateToolInstance(type) : null;
    }

    /// <summary>
    /// Checks if a tool exists.
    /// </summary>
    public bool ToolExists(string name)
    {
        return _generatedTools.ContainsKey(name) || _toolTypes.ContainsKey(name) || _contextualTools.ContainsKey(name);
    }

    /// <summary>
    /// Gets all tools by category.
    /// </summary>
    public IReadOnlyDictionary<string, List<ToolInfo>> GetToolsByCategory()
    {
        return GetListedTools().GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
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
                    try
                    {
                        BindContributionTools(contribution, contextualTool, "contextual");
                        AddContribution(result, contribution);
                    }
                    catch
                    {
                        await contribution.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    continue;
                }

                var tool = CreateAIFunction(name, context.ProjectId);
                if (tool == null)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, $"Unknown Tool '{name}'.");
                }

                result.Tools.Add(tool);
                if (IsAllowedInPlanMode(name))
                {
                    result.PlanModeAllowedToolNames.Add(tool.Name);
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
        if (_toolTypes.TryGetValue(name, out var toolType))
        {
            var tool = CreateToolInstance(toolType);
            if (tool is not IAgwTool independentTool)
            {
                throw new AgwException(ErrorCodes.InvalidParam, $"Tool '{name}' requires a project context.");
            }
            return BindTool(independentTool.ToAITool(), tool, "built-in");
        }

        if (_generatedTools.TryGetValue(name, out var generated))
        {
            return BindGeneratedTool(_generatedToolCatalog.Create(generated, Guid.Empty), generated, "attribute");
        }

        return null;
    }

    /// <summary>
    /// Creates an <see cref="AITool"/> for a tool by its name, binding it to a specific project.
    /// </summary>
    public AITool? CreateAIFunction(string name, Guid projectId)
    {
        if (_toolTypes.TryGetValue(name, out var toolType))
        {
            var tool = CreateToolInstance(toolType);
            if (tool is IProjectScopedAgwTool projectScoped)
            {
                return BindTool(projectScoped.ToAITool(projectId), tool, "built-in");
            }

            return BindTool(((IAgwTool)tool).ToAITool(), tool, "built-in");
        }

        if (_generatedTools.TryGetValue(name, out var generated))
        {
            return BindGeneratedTool(_generatedToolCatalog.Create(generated, projectId), generated, "attribute");
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

    private IAgwToolMeta CreateToolInstance(Type type)
    {
        return (IAgwToolMeta)CreateDefinitionInstance(type);
    }

    private IEnumerable<TTool> DiscoverInstances<TTool>(IEnumerable<Assembly> assemblies)
    {
        foreach (
            var type in assemblies
                .SelectMany(GetLoadableTypes)
                .Where(type =>
                    !type.IsAbstract
                    && !type.IsInterface
                    && !type.ContainsGenericParameters
                    && typeof(TTool).IsAssignableFrom(type)
                    && type != typeof(GeneratedToolBlock)
                )
        )
        {
            if (CreateDefinitionInstance(type) is TTool instance)
            {
                yield return instance;
            }
        }
    }

    private object CreateDefinitionInstance(Type type)
    {
        try
        {
            return ActivatorUtilities.GetServiceOrCreateInstance(_serviceProvider, type);
        }
        catch (AgwException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgwException(
                ErrorCodes.CannotCreateInstance,
                $"Cannot create Tool definition '{type.FullName}'.",
                exception
            );
        }
    }

    private static IReadOnlyList<Assembly> GetToolAssemblies() => [typeof(ToolRegistryService).Assembly];

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            throw new AgwException(
                ErrorCodes.CannotCreateInstance,
                $"Unable to inspect Tool assembly '{assembly.FullName}': {exception.Message}"
            );
        }
    }

    private static ToolInfo BuildRegisteredToolInfo(IAgwToolMeta tool)
    {
        var toolType = tool.GetType();
        var executeMethod = ResolveExecuteMethod(toolType);

        return new ToolInfo
        {
            Name = tool.Name,
            DisplayName = toolType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? tool.Name,
            Description = ResolveDescription(tool, executeMethod),
            Category = ResolveCategory(tool),
            TypeName = toolType.FullName ?? toolType.Name,
            Parameters = executeMethod == null ? [] : BuildParameters(executeMethod.GetParameters()),
            IsAsync = executeMethod != null && IsAsyncReturnType(executeMethod.ReturnType),
            RequiredPermission = tool.RequiredPermission,
            RequiresConfirmation = AgwToolMetadataBinding.RequiresApproval(tool.RequiredPermission),
            RequiresWorkspace = toolType.IsDefined(typeof(AgwToolRequiresWorkspaceAttribute), inherit: true),
            TimeoutMs = ResolveTimeoutMs(tool),
            ExcludeFromList = toolType.IsDefined(typeof(ExcludeToolFromListAttribute), inherit: false),
        };
    }

    private static ToolInfo BuildGeneratedToolInfo(AgwGeneratedToolDescriptor tool) =>
        new()
        {
            Name = tool.Name,
            DisplayName = tool.DisplayName,
            Description = tool.Description,
            Category = tool.Category,
            TypeName = tool.DeclaringType.FullName ?? tool.DeclaringType.Name,
            Parameters = tool
                .Parameters.Select(static parameter => new ToolParameterInfo
                {
                    Name = parameter.Name,
                    Type = parameter.Type,
                    Description = parameter.Description,
                    IsOptional = parameter.IsOptional,
                    DefaultValue = parameter.DefaultValue,
                    SchemaType = parameter.SchemaType,
                    Format = parameter.Format,
                    EnumValues = parameter.EnumValues,
                })
                .ToArray(),
            IsAsync = tool.IsAsync,
            RequiredPermission = tool.RequiredPermission,
            RequiresConfirmation = AgwToolMetadataBinding.RequiresApproval(tool.RequiredPermission),
            RequiresWorkspace = tool.RequiresWorkspace,
            TimeoutMs = tool.TimeoutMs,
            ExcludeFromList = tool.ExcludeFromList,
        };

    private static ToolInfo BuildMetaToolInfo(IContextualTool tool)
    {
        var toolType = tool.GetType();
        return new ToolInfo
        {
            Name = tool.Name,
            DisplayName = toolType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? tool.Name,
            Description = toolType.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            Category = tool.Category,
            TypeName = toolType.FullName ?? toolType.Name,
            Parameters = [],
            RequiredPermission = tool.RequiredPermission,
            RequiresConfirmation = AgwToolMetadataBinding.RequiresApproval(tool.RequiredPermission),
            RequiresWorkspace = toolType.IsDefined(typeof(AgwToolRequiresWorkspaceAttribute), inherit: true),
            ExcludeFromList = toolType.IsDefined(typeof(ExcludeToolFromListAttribute), inherit: false),
        };
    }

    private void ValidateCatalog()
    {
        foreach (var contextualTool in _contextualTools.Values)
        {
            if (_toolInfos.ContainsKey(contextualTool.Name))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool '{contextualTool.Name}' is registered more than once."
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
            if (!result.TryAdd(tool.Name, tool))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Contextual Tool '{tool.Name}' is registered more than once."
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
            RequiredPermission = null,
            RequiresConfirmation = descriptor.MayRequireApproval,
            ExcludeFromList = descriptor.ExcludeFromList,
        };

    private static void AddContribution(ToolContribution destination, ToolContribution contribution)
    {
        destination.Tools.AddRange(contribution.Tools);
        destination.PlanModeAllowedToolNames.UnionWith(contribution.PlanModeAllowedToolNames);
        destination.ContextProviders.AddRange(contribution.ContextProviders);
        destination.LoopEvaluators.AddRange(contribution.LoopEvaluators);
        destination.AutoApprovalRules.AddRange(contribution.AutoApprovalRules);
        foreach (var metadata in contribution.DynamicToolMetadata)
        {
            destination.DynamicToolMetadata.Add(metadata.Key, metadata.Value);
        }
        destination.Warnings.AddRange(contribution.Warnings);
        foreach (var warning in contribution.InvocationWarnings)
        {
            destination.InvocationWarnings[warning.Key] = warning.Value;
        }

        destination.AddResource(contribution);
    }

    private bool IsAllowedInPlanMode(string name)
    {
        if (_toolTypes.TryGetValue(name, out var toolType))
        {
            var tool = CreateToolInstance(toolType);
            return tool.AllowInPlanMode;
        }

        if (_generatedTools.TryGetValue(name, out var generatedTool))
        {
            return generatedTool.AllowInPlanMode;
        }

        return false;
    }

    private static MethodInfo? ResolveExecuteMethod(Type toolType)
    {
        return toolType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Instance)
            ?? toolType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);
    }

    private static string ResolveDescription(IAgwToolMeta tool, MethodInfo? executeMethod)
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
            ?? toolType.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? string.Empty;
    }

    private static string ResolveCategory(IAgwToolMeta tool)
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

    private static AITool BindTool(AITool tool, IAgwToolMeta metadata, string source) =>
        AgwToolMetadataBinding.Bind(
            tool,
            new AgwToolMetadata(source, metadata.RequiredPermission, metadata.AllowInPlanMode)
        );

    internal static AITool BindGeneratedTool(AITool tool, AgwGeneratedToolDescriptor descriptor, string source) =>
        AgwToolMetadataBinding.Bind(
            tool,
            new AgwToolMetadata(source, descriptor.RequiredPermission, descriptor.AllowInPlanMode)
        );

    private static void BindContributionTools(ToolContribution contribution, IAgwToolMeta metadata, string source)
    {
        for (var index = 0; index < contribution.Tools.Count; index++)
        {
            contribution.Tools[index] = BindTool(contribution.Tools[index], metadata, source);
        }
    }

    private static int ResolveTimeoutMs(IAgwToolMeta tool)
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
                SchemaType = p.GetCustomAttribute<AgwToolParameterSchemaAttribute>()?.Type,
                Format = p.GetCustomAttribute<AgwToolParameterSchemaAttribute>()?.Format,
                EnumValues = ParseEnumValues(p.GetCustomAttribute<AgwToolParameterSchemaAttribute>()?.EnumValues),
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
