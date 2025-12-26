using DSystem.Domain.Attributes;
using DSystem.Domain.Models;
using System.ComponentModel;
using System.Reflection;

namespace DSystem.Domain.Services;

/// <summary>
/// Service for discovering and managing tools available to agents.
/// </summary>
public class ToolRegistryService
{
    private readonly Dictionary<string, MethodInfo> _tools = new();
    private readonly Dictionary<string, ToolInfo> _toolInfos = new();

    public ToolRegistryService()
    {
        DiscoverTools();
    }

    /// <summary>
    /// Discovers all methods marked with ToolAttribute in the DSystem.Domain.Tools namespace.
    /// </summary>
    private void DiscoverTools()
    {
        var assembly = typeof(ToolAttribute).Assembly;
        var toolTypes = assembly.GetTypes()
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("DSystem.Domain.Tools"));

        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<ToolAttribute>() != null);

            foreach (var method in methods)
            {
                var toolAttr = method.GetCustomAttribute<ToolAttribute>()!;
                var methodName = method.Name;

                _tools[methodName] = method;

                var parameters = method.GetParameters()
                    .Select(p => new ToolParameterInfo
                    {
                        Name = p.Name ?? "param",
                        Type = p.ParameterType.Name,
                        Description = p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        IsOptional = p.IsOptional
                    })
                    .ToList();

                _toolInfos[methodName] = new ToolInfo
                {
                    Name = methodName,
                    Description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                    Category = toolAttr.Category,
                    TypeName = type.FullName ?? type.Name,
                    Parameters = parameters
                };
            }
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
        return _tools.TryGetValue(name, out var method) ? method : null;
    }

    /// <summary>
    /// Checks if a tool exists.
    /// </summary>
    public bool ToolExists(string name)
    {
        return _tools.ContainsKey(name);
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
}
