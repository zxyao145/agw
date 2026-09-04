using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Executions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed class AgentflowCheckpointPersistenceTests
{
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
        var persistence = new AgentflowCheckpointPersistence(dbContext);
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
