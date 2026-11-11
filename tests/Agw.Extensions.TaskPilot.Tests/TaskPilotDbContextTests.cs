using Agw.Extensions.Sdk.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agw.Extensions.TaskPilot.Tests;

public sealed class TaskPilotDbContextTests
{
    private static readonly Guid ExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a101");

    [Fact]
    public async Task SaveChanges_StampsOwnerAndFiltersDataByCurrentUser()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (TaskPilotDbContext db = CreateDbContext(connection, "alice"))
        {
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var rule = new TaskPilotRule
            {
                Name = "Daily review",
                GroupKey = "reviews",
                ProjectId = Guid.CreateVersion7(),
                MaxConcurrency = 2,
                Version = 1,
            };
            db.Rules.Add(rule);
            db.Matters.Add(new TaskPilotMatter { RootId = rule.Id, MatterKey = "ticket:42" });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal("alice", rule.CreateBy);
            Assert.NotEqual(default, rule.CreateTime);
            Assert.Equal(rule.Id, (await db.Matters.SingleAsync(TestContext.Current.CancellationToken)).RootId);
            Assert.Equal($"zext_{ExtensionId:N}_rule", db.Model.FindEntityType(typeof(TaskPilotRule))!.GetTableName());
            Assert.Equal(
                "INTEGER",
                db.Model.FindEntityType(typeof(TaskPilotRule))!
                    .FindProperty(nameof(TaskPilotRule.CreateTime))!
                    .GetColumnType()
            );
        }

        await using TaskPilotDbContext bobDb = CreateDbContext(connection, "bob");

        Assert.Empty(await bobDb.Rules.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await bobDb.Matters.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static TaskPilotDbContext CreateDbContext(SqliteConnection connection, string? userId)
    {
        var options = new DbContextOptionsBuilder<TaskPilotDbContext>().UseSqlite(connection).Options;
        return new TaskPilotDbContext(
            options,
            new ExtensionDatabaseIdentity(ExtensionId, Guid.CreateVersion7(), 0),
            new TestExtensionUserContext(userId, Guid.CreateVersion7()),
            TimeProvider.System
        );
    }

    private sealed record TestExtensionUserContext(string? UserId, Guid? InvocationId) : IExtensionUserContext;
}
