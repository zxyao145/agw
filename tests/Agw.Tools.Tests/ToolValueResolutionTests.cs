using Agw.Shared.Exceptions;

namespace Agw.Tools.Tests;

public sealed class ToolValueResolutionTests
{
    [Fact]
    public void Resolve_ProjectNull_ReturnsAgentDefinitions()
    {
        ToolValueObject[] agent =
        [
            Block(new TodoToolBlockDefinition()),
            Block(new BackgroundAgentsToolBlockDefinition()),
        ];

        var resolved = ToolValueResolution.Resolve(agent, null);

        Assert.Equal(
            [ToolBlockNames.Todo, ToolBlockNames.BackgroundAgents],
            resolved.ToolBlocks.Select(static definition => definition.GetDefinitionName())
        );
    }

    [Fact]
    public void Resolve_ProjectEmpty_PreservesAgentDefinitions()
    {
        ToolValueObject[] agent = [Tool(new WebSearchToolDefinition()), Block(new TodoToolBlockDefinition())];

        var resolved = ToolValueResolution.Resolve(agent, []);

        Assert.Equal(
            [ToolDefinitionNames.WebSearch],
            resolved.Tools.Select(static definition => definition.GetDefinitionName())
        );
        Assert.Equal(
            [ToolBlockNames.Todo],
            resolved.ToolBlocks.Select(static definition => definition.GetDefinitionName())
        );
    }

    [Fact]
    public void Resolve_ProjectValues_AreUnionedWithAgentValues()
    {
        ToolValueObject[] agent = [Tool(new WebSearchToolDefinition()), Block(new TodoToolBlockDefinition())];
        ToolValueObject[] project = [Tool(new WebFetchToolDefinition()), Block(new FileAccessToolBlockDefinition())];

        var resolved = ToolValueResolution.Resolve(agent, project);

        Assert.Equal(
            [ToolDefinitionNames.WebSearch, ToolDefinitionNames.WebFetch],
            resolved.Tools.Select(static definition => definition.GetDefinitionName())
        );
        Assert.Equal(
            [ToolBlockNames.Todo, ToolBlockNames.FileAccess],
            resolved.ToolBlocks.Select(static definition => definition.GetDefinitionName())
        );
    }

    [Fact]
    public void Resolve_ProjectDefinition_WithSameName_OverridesAgentDefinition()
    {
        ToolValueObject[] agent =
        [
            Block(
                new ProjectMemoryToolBlockDefinition
                {
                    Options = new ProjectMemoryToolBlockOptions { Storage = ProjectMemoryStorage.Database },
                }
            ),
        ];
        ToolValueObject[] project =
        [
            Block(
                new ProjectMemoryToolBlockDefinition
                {
                    Options = new ProjectMemoryToolBlockOptions { Storage = ProjectMemoryStorage.FileSystem },
                }
            ),
        ];

        var resolved = ToolValueResolution.Resolve(agent, project);

        var definition = Assert.IsType<ProjectMemoryToolBlockDefinition>(Assert.Single(resolved.ToolBlocks));
        Assert.Equal(ProjectMemoryStorage.FileSystem, definition.Options.Storage);
    }

    [Fact]
    public void Resolve_ProjectContainsAgentOnlyDefinition_Throws()
    {
        ToolValueObject[] project = [Block(new BackgroundAgentsToolBlockDefinition())];

        var exception = Assert.Throws<AgwException>(() => ToolValueResolution.Resolve(null, project));

        Assert.Contains("only supported by Agent definitions", exception.Message);
    }

    [Fact]
    public void Resolve_DuplicateNameWithinOwner_Throws()
    {
        ToolValueObject[] agent = [Tool(new WebSearchToolDefinition()), Tool(new WebSearchToolDefinition())];

        var exception = Assert.Throws<AgwException>(() => ToolValueResolution.Resolve(agent, null));

        Assert.Contains("duplicated", exception.Message);
    }

    private static ToolValue Tool(ToolDefinition definition) => new() { Definition = definition };

    private static ToolBlockValue Block(ToolBlockDefinition definition) => new() { Definition = definition };
}
