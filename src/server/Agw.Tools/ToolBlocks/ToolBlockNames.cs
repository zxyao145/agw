namespace Agw.Tools.ToolBlocks;

/// <summary>
/// Provides runtime names for configurable Tool Blocks.
/// </summary>
/// <remarks>
/// Registry descriptors, resolution, and runtime composition use these names. Each name aliases
/// its persisted <c>ToolBlockDefinitionNames</c> value so runtime lookup and serialized definitions cannot
/// drift apart.
/// </remarks>
public static class ToolBlockNames
{
    public const string Todo = ToolBlockDefinitionNames.Todo;
    public const string Mode = ToolBlockDefinitionNames.Mode;
    public const string ProjectMemory = ToolBlockDefinitionNames.ProjectMemory;
    public const string FileAccess = ToolBlockDefinitionNames.FileAccess;
    public const string BackgroundAgents = ToolBlockDefinitionNames.BackgroundAgents;
}
