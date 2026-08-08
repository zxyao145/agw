using System.Text.Json;

using Agw.Domain.Services;
using Agw.Shared.Contracts.Tools;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;
using Agw.Tools.ContextualTools;
using Agw.Tools.ContextualTools.Shell;
using Agw.Tools.ContextualTools.WebSearch;
using Agw.Tools.ToolBlocks.Blocks.Todo;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tools.Tests;

public class ToolRegistryServiceTests
{
    [Fact]
    public void CreateAIFunction_ForRegisteredParameterObjectTool_ExposesFlattenedParameterSchema()
    {
        var tool = CreateDiffFunction();
        var schemaText = tool.JsonSchema.GetRawText();

        Assert.Contains("before", schemaText);
        Assert.Contains("after", schemaText);
        Assert.DoesNotContain("toolParams", schemaText);
    }

    [Fact]
    public async Task InvokeAsync_ForRegisteredParameterObjectTool_AcceptsFlattenedArguments()
    {
        var tool = CreateDiffFunction();
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["before"] = "line 1",
            ["after"] = "line 2"
        });

        var result = await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);
        var resultJson = Assert.IsType<JsonElement>(result);

        Assert.Contains("- line 1", resultJson.GetProperty("result").GetString());
        Assert.Contains("+ line 2", resultJson.GetProperty("result").GetString());
    }

    [Fact]
    public void RemovedLocalTools_AreNotRegistered()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();
        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, sp);

        var removedToolNames = new[]
        {
            "read_file",
            "write_file",
            "file_edit",
            "ls",
            "glob",
            "grep",
            "task_create",
            "task_get",
            "task_list",
            "task_update",
            "task_output",
            "task_stop"
        };

        Assert.All(removedToolNames, name => Assert.False(registry.ToolExists(name)));
        Assert.True(registry.ToolExists("bash"));
        Assert.True(registry.ToolExists("powershell"));
    }

    [Fact]
    public void GetAllTools_ReturnsToolsAndToolBlocksInOneCatalog()
    {
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();
        var toolBlockRegistry = new ToolBlockRegistry([new TodoToolBlock()]);
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            [new WebSearchContextualTool()],
            toolBlockRegistry);

        var webSearch = Assert.Single(
            registry.GetAllTools(),
            item => item.Name == "web_search");
        var todo = Assert.Single(
            registry.GetAllTools(),
            item => item.Name == ToolBlockNames.Todo);

        Assert.Equal(ToolCatalogItemKind.Tool, webSearch.Kind);
        Assert.Equal(ToolCatalogItemKind.ToolBlock, todo.Kind);
        Assert.Contains("todos_add", todo.MemberToolNames);
    }

    [Fact]
    public async Task MaterializeAsync_ToolBlockMemberSelectedDirectly_ThrowsClearError()
    {
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            Array.Empty<IContextualTool>(),
            new ToolBlockRegistry([new TodoToolBlock()]));

        var exception = await Assert.ThrowsAsync<AgwException>(
            async () => await registry.MaterializeAsync(
                [new TestToolDefinition("todos_add")],
                CreateMaterializationContext(),
                TestContext.Current.CancellationToken));

        Assert.Contains("belongs to Tool Block 'todo'", exception.Message);
    }

    [Fact]
    public void ValidateDefinitionCoverage_AllDeclaredDefinitionsHaveExecutableImplementations()
    {
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();
        var contextualTools = new IContextualTool[]
        {
            new WebSearchContextualTool(),
            new ShellContextualTool(new ConfigurationBuilder().Build())
        };
        var toolBlocks = ToolBlockDefinitionNames.All
            .Select(static name => new DefinitionCoverageToolBlock(name))
            .ToArray();
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            contextualTools,
            new ToolBlockRegistry(toolBlocks));

        registry.ValidateDefinitionCoverage();
    }

    private static AIFunction CreateDiffFunction()
    {
        var services = new ServiceCollection();
        using var sp = services.BuildServiceProvider();

        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, sp);
        return Assert.IsAssignableFrom<AIFunction>(registry.CreateAIFunction("diff"));
    }

    private static ToolMaterializationContext CreateMaterializationContext()
    {
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Workspace = "/workspace"
        };
        return new ToolMaterializationContext
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = project,
            Workspace = project.Workspace,
            DefaultMode = "plan"
        };
    }

    private sealed record TestToolDefinition : ToolDefinition<EmptyToolOptions>
    {
        private readonly string _name;

        public TestToolDefinition(string name)
        {
            _name = name;
        }

        public override string GetDefinitionName() => _name;
    }

    private sealed class DefinitionCoverageToolBlock : IToolBlock
    {
        public DefinitionCoverageToolBlock(string name)
        {
            Descriptor = new ToolBlockDescriptor(
                name,
                name,
                name,
                ToolBlockScope.Agent | ToolBlockScope.Project,
                []);
        }

        public ToolBlockDescriptor Descriptor { get; }

        public ValueTask<ToolContribution> MaterializeAsync(
            ToolBlockDefinition definition,
            ToolMaterializationContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ToolContribution());
    }
}
