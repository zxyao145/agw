using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Tools.ContextualTools.WebSearch;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class WebSearchContextualToolTests
{
    [Fact]
    public async Task MaterializeAsync_HostedSupported_UsesHostedMarker()
    {
        await using var contribution = await new WebSearchContextualTool().MaterializeAsync(
            new WebSearchToolDefinition(),
            CreateContext(supportsHosted: true),
            TestContext.Current.CancellationToken
        );

        Assert.IsType<HostedWebSearchTool>(Assert.Single(contribution.Tools));
        Assert.Empty(contribution.Warnings);
        Assert.Empty(contribution.InvocationWarnings);
    }

    [Fact]
    public async Task MaterializeAsync_HostedUnsupported_RegistersInvocationWarning()
    {
        await using var contribution = await new WebSearchContextualTool().MaterializeAsync(
            new WebSearchToolDefinition(),
            CreateContext(supportsHosted: false),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("web_search", Assert.Single(contribution.Tools).Name);
        Assert.Empty(contribution.Warnings);
        var warning = Assert.Single(contribution.InvocationWarnings);
        Assert.Equal("web_search", warning.Key);
        Assert.Contains("using local search", warning.Value);
    }

    [Fact]
    public void Descriptor_IsAnIndependentTool()
    {
        var descriptor = new WebSearchContextualTool().Descriptor;

        Assert.Equal("web_search", descriptor.Name);
        Assert.Equal(Agw.Tools.Contracts.ToolCatalogItemKind.Tool, descriptor.Kind);
        Assert.Empty(descriptor.MemberToolNames);
    }

    private static ToolMaterializationContext CreateContext(bool supportsHosted) =>
        new()
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = new Project { Id = Guid.CreateVersion7(), Workspace = "/workspace" },
            Workspace = "/workspace",
            DefaultMode = "plan",
            SupportsHostedWebSearch = supportsHosted,
        };
}
