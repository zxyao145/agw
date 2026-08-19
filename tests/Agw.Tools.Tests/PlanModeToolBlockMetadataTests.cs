using System.Runtime.CompilerServices;
using Agw.Files.Abstracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Tools;
using Agw.Tools.ToolBlocks.Blocks.BackgroundAgents;
using Agw.Tools.ToolBlocks.Blocks.FileAccess;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Tests;

public sealed class PlanModeToolBlockMetadataTests
{
    [Fact]
    public async Task FileAccess_MarksOnlyReadToolsAllowedInPlan()
    {
        await using var contribution = await new FileAccessToolBlock(new UnusedFileSystemResolver()).MaterializeAsync(
            new FileAccessToolBlockDefinition(),
            CreateContext(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            ["file_access_grep", "file_access_ls", "file_access_read"],
            contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task BackgroundAgents_MarksOnlyResultReadsAllowedInPlan()
    {
        var childAgent = new ChatClientAgent(
            new StubChatClient(),
            new ChatClientAgentOptions { Id = "child-agent", Name = "Child agent" }
        );
        var context = CreateContext();
        context = new ToolMaterializationContext
        {
            Agent = context.Agent,
            Project = context.Project,
            Workspace = context.Workspace,
            DefaultMode = context.DefaultMode,
            BackgroundAgents = [childAgent],
        };

        await using var contribution = await new BackgroundAgentsToolBlock().MaterializeAsync(
            new BackgroundAgentsToolBlockDefinition(),
            context,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            ["background_agents_get_all_tasks", "background_agents_get_task_results"],
            contribution.PlanModeAllowedToolNames.Order(StringComparer.Ordinal)
        );
    }

    private static ToolMaterializationContext CreateContext()
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

    private sealed class UnusedFileSystemResolver : IAgwFileSystemResolver
    {
        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct) =>
            throw new InvalidOperationException("The resolver should not be used during materialization.");
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }
    }
}
