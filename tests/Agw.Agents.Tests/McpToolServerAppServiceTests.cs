using Agw.Agents.Definitions.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agents;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public class McpToolServerAppServiceTests
{
    [Fact]
    public async Task CreateAndUpdateAsync_NormalizesCollectionsAndBindingsAndPersistsAudit()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var user = new TestUserInfoService();
        var createdAt = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
        var clock = new TestTimeProvider(createdAt);
        var auditUser = new AuditUserIdProvider();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(
                new EntityCreatorInterceptor(auditUser, clock),
                new EntityModifierInterceptor(auditUser, clock)
            )
            .Options;
        await using var context = new AgwDbContext(options);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var agent = new Agent { Name = "agent", Type = AgentType.External };
        context.Agents.Add(agent);
        await context.SaveChangesAsync(cancellationToken);
        var service = new McpToolServerAppService(context, user);

        // Act
        var server = await service.CreateMcpToolServerAsync(
            new McpServer
            {
                Name = "server",
                Arguments = null!,
                EnvironmentVariables = null!,
                Headers = null!,
            },
            [Guid.Empty, agent.Id, agent.Id],
            "tester"
        );

        // Assert
        Assert.NotEqual(Guid.Empty, server.Id);
        Assert.Equal("tester", server.CreateBy);
        Assert.Equal(createdAt, server.CreateTime);
        Assert.Empty(server.Arguments);
        Assert.Empty(server.EnvironmentVariables);
        Assert.Empty(server.Headers);
        var link = await context.AgentMcpToolServers.SingleAsync(cancellationToken);
        Assert.Equal(agent.Id, link.AgentId);
        Assert.Equal(server.Id, link.McpToolServerId);

        for (var update = 1; update <= 2; update++)
        {
            context.ChangeTracker.Clear();
            var updatedAt = createdAt.AddMinutes(update);
            clock.SetUtcNow(updatedAt);
            await service.UpdateMcpToolServerAsync(
                server.Id,
                current =>
                {
                    current.Name = $"server-{update}";
                    current.Arguments = null!;
                    current.Headers = new Dictionary<string, string> { ["Header"] = $"{update}" };
                },
                "tester"
            );
            context.ChangeTracker.Clear();
            var persisted = await context.McpToolServers.SingleAsync(cancellationToken);

            Assert.Equal($"server-{update}", persisted.Name);
            Assert.Empty(persisted.Arguments);
            Assert.Equal($"{update}", persisted.Headers["Header"]);
            Assert.Equal("tester", persisted.CreateBy);
            Assert.Equal(createdAt, persisted.CreateTime);
            Assert.Equal("tester", persisted.UpdateBy);
            Assert.Equal(updatedAt, persisted.UpdateTime);
        }
    }

    private sealed class AuditUserIdProvider : IEntityAuditUserIdProvider
    {
        public string GetUserId() => UserInfoUtil.RequiredUserId;
    }
}
