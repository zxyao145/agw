using Agw.Extensions.Sdk.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agw.Extensions.Feishu.Tests;

public sealed class FeishuDbContextTests
{
    [Fact]
    public async Task Model_OwnedRowsAndUniqueIndexes_AreCreatedInExtensionNamespace()
    {
        Guid extensionId = FeishuExtension.ExtensionId;
        var userContext = new TestUserContext("user-1");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<FeishuDbContext>().UseSqlite(connection).Options;
        await using var db = new FeishuDbContext(
            options,
            new ExtensionDatabaseIdentity(extensionId, Guid.CreateVersion7(), 1),
            userContext,
            TimeProvider.System
        );

        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        db.Apps.Add(
            new FeishuApp
            {
                Id = Guid.CreateVersion7(),
                Name = "Test",
                Mode = FeishuAppMode.App,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        string tableName = $"zext_{extensionId:N}_app";
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "user-1",
            await db.Apps.Select(app => app.CreateBy).SingleAsync(TestContext.Current.CancellationToken)
        );
    }

    private sealed class TestUserContext(string? userId) : IExtensionUserContext
    {
        public string? UserId => userId;

        public Guid? InvocationId => Guid.CreateVersion7();
    }
}
