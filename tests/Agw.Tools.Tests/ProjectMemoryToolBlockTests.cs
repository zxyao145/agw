using System.Text.Json;

using Agw.Files.Abstracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Tools.ToolBlocks.Blocks.ProjectMemory;

using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Tests;

public sealed class ProjectMemoryToolBlockTests
{
    [Fact]
    public async Task MaterializeAsync_MissingConversation_CreatesStatelessProvider()
    {
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var block = new ProjectMemoryToolBlock(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            new UnusedFileSystemResolver());

        await using var contribution = await block.MaterializeAsync(
            new ProjectMemoryToolBlockDefinition(),
            CreateContext(Guid.Empty),
            TestContext.Current.CancellationToken);

        var provider = Assert.IsType<ProjectMemoryProvider>(
            Assert.Single(contribution.ContextProviders));
        Assert.Empty(provider.StateKeys);
        Assert.Equal(
            [
                ProjectMemoryProvider.GrepToolName,
                ProjectMemoryProvider.LsToolName,
                ProjectMemoryProvider.ReadFileToolName
            ],
            contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal));
        Assert.Empty(contribution.Warnings);
    }

    [Fact]
    public async Task MaterializeAsync_PersistedConversation_CreatesProvider()
    {
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var block = new ProjectMemoryToolBlock(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            new UnusedFileSystemResolver());

        await using var contribution = await block.MaterializeAsync(
            new ProjectMemoryToolBlockDefinition(),
            CreateContext(Guid.CreateVersion7()),
            TestContext.Current.CancellationToken);

        Assert.Single(contribution.ContextProviders);
        Assert.Empty(contribution.Warnings);
    }

    [Fact]
    public void Deserialize_MissingOptions_Throws()
    {
        Assert.Throws<JsonException>(() => ToolValueObjectJson.Deserialize(
            """[{"kind":"toolBlock","definition":{"name":"project-memory"}}]"""));
    }

    [Fact]
    public void Serialize_FileSystemStorage_UsesStableJsonValue()
    {
        var json = ToolValueObjectJson.Serialize(
        [
            new ToolBlockValue
            {
                Definition = new ProjectMemoryToolBlockDefinition
                {
                    Options = new ProjectMemoryToolBlockOptions
                    {
                        Storage = ProjectMemoryStorage.FileSystem
                    }
                }
            }
        ]);
        using var document = JsonDocument.Parse(json);
        var value = Assert.Single(document.RootElement.EnumerateArray());
        var definition = value.GetProperty("definition");

        Assert.Equal("toolBlock", value.GetProperty("kind").GetString());
        Assert.Equal("project-memory", definition.GetProperty("name").GetString());
        Assert.Equal(
            "filesystem",
            definition.GetProperty("options").GetProperty("storage").GetString());
    }

    [Fact]
    public void FileSystemRoot_UsesSharedWorkspaceMemoryDirectory()
    {
        Assert.Equal(".agw/memory", ProjectMemoryToolBlock.FileSystemRoot);
    }

    [Fact]
    public void GetMutationResourceName_FileSystemUsesWorkspaceInsteadOfProjectId()
    {
        var first = CreateContext(Guid.CreateVersion7(), "/shared/workspace");
        var second = CreateContext(Guid.CreateVersion7(), "/shared/workspace");

        var firstResource = ProjectMemoryToolBlock.GetMutationResourceName(
            first,
            ProjectMemoryStorage.FileSystem);
        var secondResource = ProjectMemoryToolBlock.GetMutationResourceName(
            second,
            ProjectMemoryStorage.FileSystem);

        Assert.Equal(firstResource, secondResource);
    }

    private static ToolMaterializationContext CreateContext(
        Guid conversationId,
        string workspace = "/workspace")
    {
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Name = $"project-{Guid.CreateVersion7():N}",
            Workspace = workspace
        };
        return new ToolMaterializationContext
        {
            Agent = new Agent { Id = Guid.CreateVersion7() },
            Project = project,
            ConversationId = conversationId,
            Workspace = project.Workspace,
            DefaultMode = "plan"
        };
    }

    private sealed class UnusedFileSystemResolver : IAgwFileSystemResolver
    {
        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct) =>
            throw new InvalidOperationException("The resolver should not be used during materialization.");
    }
}
