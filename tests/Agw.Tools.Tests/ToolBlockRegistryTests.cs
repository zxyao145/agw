using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;

namespace Agw.Tools.Tests;

public sealed class ToolBlockRegistryTests
{
    [Fact]
    public void Constructor_DuplicateIds_Throws()
    {
        var blocks = new[]
        {
            new RecordingToolBlock("duplicate", ToolBlockScope.Agent),
            new RecordingToolBlock("duplicate", ToolBlockScope.Agent),
        };

        var exception = Assert.Throws<AgwException>(() => new ToolBlockRegistry(blocks));

        Assert.Contains("registered more than once", exception.Message);
    }

    [Fact]
    public async Task MaterializeAsync_NoDefinitions_ProducesNoRuntimeContribution()
    {
        var block = new RecordingToolBlock("test", ToolBlockScope.Agent);
        var registry = new ToolBlockRegistry([block]);

        await using var contribution = await registry.MaterializeAsync(
            [],
            ToolBlockScope.Agent,
            CreateContext(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(0, block.MaterializationCount);
        Assert.Empty(contribution.Tools);
        Assert.Empty(contribution.ContextProviders);
        Assert.Empty(contribution.LoopEvaluators);
        Assert.Empty(contribution.AutoApprovalRules);
    }

    [Fact]
    public void Constructor_DuplicateMemberNames_Throws()
    {
        var blocks = new[]
        {
            new RecordingToolBlock("first", ToolBlockScope.Agent, ["shared"]),
            new RecordingToolBlock("second", ToolBlockScope.Agent, ["shared"]),
        };

        var exception = Assert.Throws<AgwException>(() => new ToolBlockRegistry(blocks));

        Assert.Contains("belongs to more than one Tool Block", exception.Message);
    }

    [Fact]
    public async Task MaterializeAsync_UnknownDefinition_Throws()
    {
        var registry = new ToolBlockRegistry([]);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await registry.MaterializeAsync(
                [new TestToolBlockDefinition("unknown")],
                ToolBlockScope.Agent,
                CreateContext(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("Unknown Tool Block", exception.Message);
    }

    [Fact]
    public async Task MaterializeAsync_UnsupportedScope_ThrowsWithoutMaterializing()
    {
        var block = new RecordingToolBlock("agent-only", ToolBlockScope.Agent);
        var registry = new ToolBlockRegistry([block]);

        var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await registry.MaterializeAsync(
                [new TestToolBlockDefinition("agent-only")],
                ToolBlockScope.Project,
                CreateContext(),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Contains("not supported for scope", exception.Message);
        Assert.Equal(0, block.MaterializationCount);
    }

    [Fact]
    public async Task MaterializeAsync_BlockFails_RestoresEnabledBlockNames()
    {
        var block = new RecordingToolBlock("failing", ToolBlockScope.Agent, throwOnMaterialize: true);
        var registry = new ToolBlockRegistry([block]);
        var context = CreateContext();
        var originalNames = new HashSet<string>(["existing"], StringComparer.OrdinalIgnoreCase);
        context.EnabledToolBlockNames = originalNames;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.MaterializeAsync(
                [new TestToolBlockDefinition("failing")],
                ToolBlockScope.Agent,
                context,
                TestContext.Current.CancellationToken
            )
        );

        Assert.Same(originalNames, context.EnabledToolBlockNames);
    }

    private static ToolMaterializationContext CreateContext() =>
        new()
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = new Project { Id = Guid.CreateVersion7(), Workspace = "/workspace" },
            Workspace = "/workspace",
            DefaultMode = "plan",
        };

    private sealed class RecordingToolBlock : IToolBlock
    {
        public RecordingToolBlock(
            string id,
            ToolBlockScope scopes,
            IReadOnlyList<string>? memberToolNames = null,
            bool throwOnMaterialize = false
        )
        {
            _throwOnMaterialize = throwOnMaterialize;
            Descriptor = new ToolBlockDescriptor(id, id, id, scopes, memberToolNames ?? []);
        }

        public ToolBlockDescriptor Descriptor { get; }

        private readonly bool _throwOnMaterialize;

        public int MaterializationCount { get; private set; }

        public ValueTask<ToolContribution> MaterializeAsync(
            ToolBlockDefinition definition,
            ToolMaterializationContext context,
            CancellationToken cancellationToken
        )
        {
            MaterializationCount++;
            if (_throwOnMaterialize)
            {
                throw new InvalidOperationException("materialization failed");
            }

            return ValueTask.FromResult(new ToolContribution());
        }
    }

    private sealed record TestToolBlockDefinition : ToolBlockDefinition<EmptyToolOptions>
    {
        private readonly string _customId;

        public TestToolBlockDefinition(string customId)
        {
            _customId = customId;
        }

        public override string GetDefinitionName() => _customId;
    }
}
