using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class AgentflowAppServiceTests
{
    [Fact]
    public async Task UpdateAsync_ConcurrentOppositeReferences_OnlyOneDefinitionCommits()
    {
        var token = TestContext.Current.CancellationToken;
        using var caller = UserInfoUtil.Push(
            new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "tester")],
                    "test"
                )
            )
        );
        await using var connection = await OpenConnectionAsync(token);
        await using var seed = await CreateDbContextAsync(connection, token);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        seed.Agentflows.AddRange(
            new Agentflow
            {
                Id = first,
                Name = "first",
                CreateBy = "tester",
            },
            new Agentflow
            {
                Id = second,
                Name = "second",
                CreateBy = "tester",
            }
        );
        await seed.SaveChangesAsync(token);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Agentflow?> UpdateAsync(Guid root, Guid nested)
        {
            await start.Task;
            await using var db = new AgwDbContext(options);
            var service = CreateService(db);
            var node = Node(root, "nested", AgentflowNodeKind.WorkflowAsAgent);
            node.RelateId = nested;
            return await service.UpdateAsync(
                root,
                _ => { },
                [Node(root, "input", AgentflowNodeKind.Input), node, Node(root, "output", AgentflowNodeKind.Output)],
                [Edge(root, "input-nested", "input", "nested"), Edge(root, "nested-output", "nested", "output")],
                "tester",
                token
            );
        }
        var firstUpdate = Task.Run(() => UpdateAsync(first, second), token);
        var secondUpdate = Task.Run(() => UpdateAsync(second, first), token);
        start.SetResult();

        var results = await Task.WhenAll(firstUpdate, secondUpdate);

        Assert.Single(results, result => result != null);
        Assert.Equal(
            1,
            await seed.AgentflowNodes.CountAsync(node => node.Kind == AgentflowNodeKind.WorkflowAsAgent, token)
        );
    }
}
