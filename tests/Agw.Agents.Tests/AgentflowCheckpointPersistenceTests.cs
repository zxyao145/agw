using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Durable;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class AgentflowCheckpointPersistenceTests
{
    [Theory]
    [InlineData(typeof(AgentflowCheckpointPersistence))]
    [InlineData(typeof(ProjectDeletionCoordinator))]
    [InlineData(typeof(DurableExecutionStore))]
    public void Construction_MissingScopeMaintenance_FailsInsteadOfFallingBack(Type serviceType)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped(_ => new AgwDbContext(
            new DbContextOptionsBuilder<AgwDbContext>().UseSqlite("Data Source=:memory:").Options
        ));
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddSingleton<IApplicationLock>(InMemoryApplicationLock.Shared);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(serviceType);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService(serviceType)
        );

        // Assert
        Assert.Contains(nameof(IDurableExecutionScopeMaintenance), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_CommitFalse_DoesNotPersistStagedChanges()
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "tester")], authenticationType: "Test")
            )
        );
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        var persistence = new AgentflowCheckpointPersistence(dbContext, TestDurablePersistence.Create(dbContext));
        var checkpointId = Guid.CreateVersion7();

        // Act
        var result = await persistence.ExecuteAsync(
            (session, _) =>
            {
                session.Agents.AgentflowCheckpoints.Add(
                    new AgentflowCheckpointRecord
                    {
                        Id = checkpointId,
                        ProjectId = Guid.CreateVersion7(),
                        ProjectConversationId = Guid.CreateVersion7(),
                        ContextId = "context",
                        TaskId = Guid.CreateVersion7(),
                        AgentflowId = Guid.CreateVersion7(),
                        UserId = "tester",
                        BoundarySequence = 0,
                        DefinitionFingerprint = new string('a', 64),
                        MarkersJson = "[]",
                        CheckpointJson = "{}",
                    }
                );
                return Task.FromResult(
                    new AgentflowCheckpointPersistenceResult<string>("not-committed", Commit: false)
                );
            },
            cancellationToken
        );
        dbContext.ChangeTracker.Clear();

        // Assert
        Assert.Equal("not-committed", result);
        Assert.Null(
            await dbContext.AgentflowCheckpoints.SingleOrDefaultAsync(
                checkpoint => checkpoint.Id == checkpointId,
                cancellationToken
            )
        );
    }
}
