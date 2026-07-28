using Agw.Infrastructure.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Tests;

public sealed class ProjectConversationMigrationTests
{
    private const string PreviousMigration = "20260726135918_AddSkillKindAndRemoteCache";
    private const string RenameMigration = "20260728122531_RenameProjectConversationEntities";

    [Fact]
    public async Task MigrateAsync_Sqlite_RenamesTablesAndPreservesConversationData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var dbContext = new AgwDbContext(options);
        var migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, cancellationToken);

        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var historyId = Guid.CreateVersion7();
        var taskId = Guid.CreateVersion7();
        var bindingId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var createdAt = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO project
                (id, name, type, environment_variables, create_time)
            VALUES
                ({projectId}, {"Migration project"}, {0}, {"{}"}, {createdAt});
            """,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO project_context
                (id, project_id, context_id, title, create_time)
            VALUES
                ({conversationId}, {projectId}, {"context-1"}, {"Migration conversation"}, {createdAt});
            """,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO project_task_record
                (id, project_context_id, task_id, status, conversation_sequence, conversation_payload, create_time)
            VALUES
                ({historyId}, {conversationId}, {taskId}, {2}, {1L}, {"{\"role\":\"user\"}"}, {createdAt});
            """,
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO task_session_binding
                (id, project_context_id, agent_id, external_agent_name, provider_session_id, create_time)
            VALUES
                ({bindingId}, {conversationId}, {agentId}, {"codex"}, {"session-1"}, {createdAt});
            """,
            cancellationToken);

        await migrator.MigrateAsync(RenameMigration, cancellationToken);

        dbContext.ChangeTracker.Clear();
        var conversation = await dbContext.ProjectConversations.SingleAsync(
            item => item.Id == conversationId,
            cancellationToken);
        var history = await dbContext.ProjectConversationChatHistories.SingleAsync(
            item => item.Id == historyId,
            cancellationToken);
        var binding = await dbContext.TaskSessionBindings.SingleAsync(
            item => item.Id == bindingId,
            cancellationToken);

        Assert.Equal("context-1", conversation.ContextId);
        Assert.Equal(conversationId, history.ConversationId);
        Assert.Equal("{\"role\":\"user\"}", history.ConversationPayload);
        Assert.Equal(conversationId, binding.ProjectConversationId);
        Assert.False(await TableExistsAsync(connection, "project_context", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_task_record", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_conversation_chat_history", cancellationToken));

        await migrator.MigrateAsync(PreviousMigration, cancellationToken);

        Assert.True(await TableExistsAsync(connection, "project_context", cancellationToken));
        Assert.True(await TableExistsAsync(connection, "project_task_record", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_conversation", cancellationToken));
        Assert.False(await TableExistsAsync(connection, "project_conversation_chat_history", cancellationToken));
        Assert.Equal(
            "{\"role\":\"user\"}",
            await ReadScalarAsync(
                connection,
                "SELECT conversation_payload FROM project_task_record WHERE id = $id;",
                historyId,
                cancellationToken));
        Assert.Equal(
            conversationId,
            Guid.Parse((await ReadScalarAsync(
                connection,
                "SELECT project_context_id FROM task_session_binding WHERE id = $id;",
                bindingId,
                cancellationToken))!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_SqliteAndPostgres_UsesOnlyDataPreservingRenames(bool usePostgres)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        if (usePostgres)
        {
            options.UseNpgsql("Host=localhost;Database=agw;Username=agw;Password=unused");
        }
        else
        {
            options.UseSqlite("Data Source=:memory:");
        }

        options.UseSnakeCaseNamingConvention();
        using var dbContext = new AgwDbContext(options.Options);

        var script = dbContext.GetService<IMigrator>().GenerateScript(
            PreviousMigration,
            RenameMigration,
            MigrationsSqlGenerationOptions.NoTransactions);

        Assert.Contains("project_context", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_conversation", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_task_record", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_conversation_chat_history", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project_conversation_id", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);

        if (usePostgres)
        {
            Assert.Contains("RENAME CONSTRAINT pk_project_context", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "RENAME CONSTRAINT fk_task_session_binding_project_context_project_context_id",
                script,
                StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.DoesNotContain("ef_temp_", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<string?> ReadScalarAsync(
        SqliteConnection connection,
        string commandText,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }
}
