using System.Text.Json;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
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
        var arguments = new AIFunctionArguments(
            new Dictionary<string, object?> { ["before"] = "line 1", ["after"] = "line 2" }
        );

        var result = await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);
        var resultJson = Assert.IsType<JsonElement>(result);

        Assert.Contains("- line 1", resultJson.GetProperty("result").GetString());
        Assert.Contains("+ line 2", resultJson.GetProperty("result").GetString());
    }

    [Fact]
    public async Task MaterializeAsync_IndependentTools_MarksOnlyTrustedPlanToolsAllowed()
    {
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, serviceProvider);

        await using var contribution = await registry.MaterializeAsync(
            [
                new AskUserQuestionToolDefinition(),
                new DiffToolDefinition(),
                new GitCloneToolDefinition(),
                new WebFetchToolDefinition(),
            ],
            CreateMaterializationContext(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            ["ask_user_question", "diff", "web_fetch"],
            contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSearchMaterialization_LocalOrHosted_MarksToolAllowedInPlan(bool hosted)
    {
        var context = CreateMaterializationContext();
        context = new ToolMaterializationContext
        {
            Agent = context.Agent,
            Project = context.Project,
            Workspace = context.Workspace,
            DefaultMode = context.DefaultMode,
            SupportsHostedWebSearch = hosted,
        };

        await using var contribution = await new WebSearchContextualTool().MaterializeAsync(
            new WebSearchToolDefinition(),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["web_search"], contribution.PlanModeAllowedToolNames);
    }

    [Fact]
    public void RemovedAndObsoleteTools_AreNotRegistered()
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
            "task_stop",
            "bash",
            "generate_guid",
            "powershell",
        };

        Assert.All(removedToolNames, name => Assert.False(registry.ToolExists(name)));
        Assert.All(removedToolNames, name => Assert.Null(registry.GetTool(name)));
        Assert.Null(registry.GetToolMethod("generate_guid"));
        Assert.Null(registry.GetToolInstance("bash"));
        Assert.Null(registry.GetToolInstance("powershell"));
        Assert.DoesNotContain(registry.GetAllTools(), tool => removedToolNames.Contains(tool.Name));
    }

    [Theory]
    [InlineData(ToolDefinitionNames.Bash)]
    [InlineData(ToolDefinitionNames.GenerateGuid)]
    [InlineData(ToolDefinitionNames.PowerShell)]
    public async Task MaterializeAsync_ObsoleteToolDefinition_ThrowsClearError(string toolName)
    {
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var registry = new ToolRegistryService(NullLogger<ToolRegistryService>.Instance, serviceProvider);
        ToolDefinition definition = toolName switch
        {
            ToolDefinitionNames.Bash => new BashToolDefinition(),
            ToolDefinitionNames.GenerateGuid => new GenerateGuidToolDefinition(),
            ToolDefinitionNames.PowerShell => new PowerShellToolDefinition(),
            _ => throw new InvalidOperationException($"Unexpected Tool '{toolName}'."),
        };

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await registry.MaterializeAsync(
                [definition],
                CreateMaterializationContext(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal($"Tool '{toolName}' is obsolete and unavailable.", exception.Message);
    }

    [Fact]
    public async Task ObsoleteContextualToolAndToolBlock_AreFilteredAndUnavailable()
    {
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
#pragma warning disable CS0618
        var toolBlockRegistry = new ToolBlockRegistry([new ObsoleteTodoToolBlock()]);
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            [new ObsoleteWebSearchContextualTool()],
            toolBlockRegistry
        );
#pragma warning restore CS0618

        Assert.False(registry.ToolExists(ToolDefinitionNames.WebSearch));
        Assert.Null(registry.GetTool(ToolDefinitionNames.WebSearch));
        Assert.Null(registry.GetTool(ToolBlockNames.Todo));
        Assert.DoesNotContain(
            registry.GetAllTools(),
            tool => tool.Name is ToolDefinitionNames.WebSearch or ToolBlockNames.Todo
        );

        var contextualException = await Assert.ThrowsAsync<AgwException>(async () =>
            await registry.MaterializeAsync(
                [new WebSearchToolDefinition()],
                CreateMaterializationContext(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            $"Tool '{ToolDefinitionNames.WebSearch}' is obsolete and unavailable.",
            contextualException.Message
        );

        var toolBlockException = await Assert.ThrowsAsync<AgwException>(async () =>
            await toolBlockRegistry.MaterializeAsync(
                [new TodoToolBlockDefinition()],
                ToolBlockScope.Agent,
                CreateMaterializationContext(),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal($"Tool Block '{ToolBlockNames.Todo}' is obsolete and unavailable.", toolBlockException.Message);
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
            toolBlockRegistry
        );

        var webSearch = Assert.Single(registry.GetAllTools(), item => item.Name == "web_search");
        var todo = Assert.Single(registry.GetAllTools(), item => item.Name == ToolBlockNames.Todo);

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
            new ToolBlockRegistry([new TodoToolBlock()])
        );

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await registry.MaterializeAsync(
                [new TestToolDefinition("todos_add")],
                CreateMaterializationContext(),
                TestContext.Current.CancellationToken
            )
        );

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
            new ShellContextualTool(new ConfigurationBuilder().Build()),
        };
        var toolBlocks = ToolBlockDefinitionNames
            .All.Select(static name => new DefinitionCoverageToolBlock(name))
            .ToArray();
        var registry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            contextualTools,
            new ToolBlockRegistry(toolBlocks)
        );

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
        var project = new Project { Id = Guid.CreateVersion7(), Workspace = "/workspace" };
        return new ToolMaterializationContext
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = project,
            Workspace = project.Workspace,
            DefaultMode = "plan",
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
            Descriptor = new ToolBlockDescriptor(name, name, name, ToolBlockScope.Agent | ToolBlockScope.Project, []);
        }

        public ToolBlockDescriptor Descriptor { get; }

        public ValueTask<ToolContribution> MaterializeAsync(
            ToolBlockDefinition definition,
            ToolMaterializationContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(new ToolContribution());
    }

    [Obsolete("Test obsolete Contextual Tool")]
    private sealed class ObsoleteWebSearchContextualTool : IContextualTool
    {
        public ToolInfo Descriptor { get; } =
            new()
            {
                Name = ToolDefinitionNames.WebSearch,
                DisplayName = "Obsolete Web Search",
                Description = "Test obsolete Contextual Tool.",
                Category = "Test",
                TypeName = typeof(ObsoleteWebSearchContextualTool).FullName!,
                Parameters = [],
            };

        public ValueTask<ToolContribution> MaterializeAsync(
            ToolDefinition definition,
            ToolMaterializationContext context,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Obsolete Contextual Tool must not be materialized.");
    }

    [Obsolete("Test obsolete Tool Block")]
    private sealed class ObsoleteTodoToolBlock : IToolBlock
    {
        public ToolBlockDescriptor Descriptor { get; } =
            new(
                ToolBlockNames.Todo,
                "Obsolete Todo",
                "Test obsolete Tool Block.",
                ToolBlockScope.Agent | ToolBlockScope.Project,
                ["obsolete_todo"]
            );

        public ValueTask<ToolContribution> MaterializeAsync(
            ToolBlockDefinition definition,
            ToolMaterializationContext context,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Obsolete Tool Block must not be materialized.");
    }
}
