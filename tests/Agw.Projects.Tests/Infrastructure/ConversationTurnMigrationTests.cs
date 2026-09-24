using System.Data.Common;
using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Infrastructure.Data;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.AI;
using Npgsql;

namespace Agw.Infrastructure.Tests;

public sealed partial class InitialMigrationTests
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool PostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact]
    public async Task MigrateAsync_ConversationTurnsSqlite_BackfillsTurnsHistoryColumnsAndEventSequences()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(
                connection,
                builder => builder.MigrationsAssembly(AgwDbContextOptionsConfigurator.SqliteMigrationsAssembly)
            )
            .UseSnakeCaseNamingConvention()
            .Options;

        await AssertConversationTurnBackfillAsync(options, postgres: false);
    }

    [Fact(
        SkipUnless = nameof(PostgresEnabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    public async Task MigrateAsync_ConversationTurnsPostgres_BackfillsTurnsHistoryColumnsAndEventSequences()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        var database = $"agw_turn_migration_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            await create.ExecuteNonQueryAsync(token);
        try
        {
            settings.Database = database;
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Postgres, settings.ConnectionString);

            await AssertConversationTurnBackfillAsync(options.Options, postgres: true);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 在上一个迁移的结构中写入旧数据，执行 AddConversationTurns 后检查回填结果，再降级检查 history_scope 写回 metadata。
    /// Writes legacy rows under the previous migration's schema, checks the backfill after AddConversationTurns, then downgrades and checks that history_scope returns to metadata.
    /// </summary>
    private static async Task AssertConversationTurnBackfillAsync(DbContextOptions<AgwDbContext> options, bool postgres)
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        await using var context = new AgwDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToArray();
        var turnsMigration = Array.FindIndex(
            migrations,
            name => name.EndsWith("_AddConversationTurns", StringComparison.Ordinal)
        );
        await migrator.MigrateAsync(migrations[turnsMigration - 1], token);
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var otherConversationId = Guid.CreateVersion7();
        var agentId = Guid.CreateVersion7();
        var agentflowId = Guid.CreateVersion7();
        var scope = $"agentflow:{agentflowId:N}:node:review";
        var started = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        context.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "legacy",
                CreateBy = "tester",
                CreateTime = started,
            }
        );
        foreach (var id in new[] { conversationId, otherConversationId })
            context.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = id,
                    ProjectId = projectId,
                    ContextId = id.ToString("N"),
                    Title = "legacy",
                    CreateBy = "tester",
                    CreateTime = started,
                }
            );
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
        var firstInput = Guid.CreateVersion7();
        var nodeInput = Guid.CreateVersion7();
        var secondInput = Guid.CreateVersion7();
        var contentResult = new ChatMessage(ChatRole.Assistant, "flow result\0");
        contentResult.Contents[0].AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "result" };
        var history = new (Guid Id, Guid ConversationId, ChatMessage Message, string? Metadata)[]
        {
            (firstInput, conversationId, new ChatMessage(ChatRole.User, "first\0request"), Target("agent", agentId)),
            (
                Guid.CreateVersion7(),
                conversationId,
                new ChatMessage(ChatRole.Assistant, "answer"),
                """{"purpose":"message"}"""
            ),
            (
                Guid.CreateVersion7(),
                conversationId,
                new ChatMessage(ChatRole.Assistant, "answer\0")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = "result" },
                },
                null
            ),
            (
                nodeInput,
                conversationId,
                new ChatMessage(ChatRole.User, "upstream output")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["agentflowInput"] = true },
                },
                JsonSerializer.Serialize(new Dictionary<string, string> { ["historyScope"] = scope })
            ),
            (
                secondInput,
                conversationId,
                new ChatMessage(ChatRole.User, "second request"),
                Target("agentflow", agentflowId)
            ),
            (
                Guid.CreateVersion7(),
                conversationId,
                new ChatMessage(ChatRole.User, "handoff copy")
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["conversationHandoff"] = true },
                },
                null
            ),
            (Guid.CreateVersion7(), conversationId, contentResult, null),
        };
        for (var sequence = 0; sequence < history.Length; sequence++)
        {
            var row = history[sequence];
            await InsertHistoryAsync(
                context,
                postgres,
                row.Id,
                row.ConversationId,
                sequence,
                row.Message,
                row.Metadata,
                started.AddSeconds(sequence)
            );
        }
        await InsertHistoryAsync(
            context,
            postgres,
            Guid.CreateVersion7(),
            otherConversationId,
            0,
            new ChatMessage(ChatRole.Assistant, "without input"),
            null,
            started
        );
        var executionId = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO durable_execution (id, user_id, manifest_json, status, segment_index, state_changed_at, state_version, create_time) VALUES ({executionId}, 'tester', 'manifest', 3, 1, {started}, {Guid.CreateVersion7()}, {started})",
            token
        );
        var laterSegment = Guid.CreateVersion7();
        var firstEvent = Guid.CreateVersion7();
        var secondEvent = Guid.CreateVersion7();
        foreach (var (id, segment, sequence) in new[] { (laterSegment, 1, 0), (secondEvent, 0, 5), (firstEvent, 0, 2) })
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO execution_stream_entry (id, execution_id, segment_index, sequence, payload_json, create_time) VALUES ({id}, {executionId}, {segment}, {sequence}, 'payload', {started})",
                token
            );

        // Act
        await migrator.MigrateAsync(migrations[turnsMigration], token);

        // Assert
        var rows = await context
            .ProjectConversationChatHistories.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.ConversationId == conversationId)
            .OrderBy(row => row.ConversationSequence)
            .ToListAsync(token);
        Assert.Equal(
            [firstInput, firstInput, firstInput, firstInput, secondInput, secondInput, secondInput],
            rows.Select(row => row.TurnId)
        );
        Assert.Equal(
            [
                ConversationMessagePurpose.Input,
                ConversationMessagePurpose.Message,
                ConversationMessagePurpose.Result,
                ConversationMessagePurpose.Message,
                ConversationMessagePurpose.Input,
                ConversationMessagePurpose.Message,
                ConversationMessagePurpose.Result,
            ],
            rows.Select(row => row.Purpose)
        );
        Assert.Equal([null, null, null, scope, null, null, null], rows.Select(row => row.HistoryScope));
        Assert.Contains("\\u0000", rows[0].ConversationPayload);
        Assert.Contains("\\u0000", rows[2].ConversationPayload);
        Assert.Contains("\\u0000", rows[6].ConversationPayload);
        Assert.False(rows[3].Metadata!.ContainsKey("historyScope"));
        Assert.All(rows, row => Assert.Null(row.StepIndex));
        Assert.All(rows, row => Assert.Null(row.AgentId));
        var turns = await context
            .ProjectConversationTurns.IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(turn => turn.FirstSequence)
            .ToListAsync(token);
        Assert.Equal(2, turns.Count);
        Assert.All(turns, turn => Assert.Equal(conversationId, turn.ProjectConversationId));
        Assert.All(turns, turn => Assert.Equal(ProjectConversationTurnStatus.Completed, turn.Status));
        Assert.All(turns, turn => Assert.Equal(0, turn.StepCount));
        Assert.Equal([firstInput, secondInput], turns.Select(turn => turn.Id));
        Assert.Equal([firstInput, secondInput], turns.Select(turn => turn.InputMessageId));
        Assert.Equal([agentId, agentflowId], turns.Select(turn => turn.TargetId));
        Assert.Equal([AgentRuntimeType.Agent, AgentRuntimeType.Agentflow], turns.Select(turn => turn.RuntimeType));
        Assert.Equal([0L, 4L], turns.Select(turn => turn.FirstSequence));
        Assert.Equal([3L, 6L], turns.Select(turn => turn.LastSequence));
        Assert.Equal([started, started.AddSeconds(4)], turns.Select(turn => turn.StartedAt));
        Assert.Equal([started.AddSeconds(3), started.AddSeconds(6)], turns.Select(turn => turn.FinishedAt));
        Assert.Equal(
            secondInput,
            await context
                .ProjectConversationTurns.IgnoreQueryFilters()
                .Where(turn => turn.TargetId == agentflowId)
                .Select(turn => turn.Id)
                .SingleAsync(token)
        );
        Assert.Null(
            await context
                .ProjectConversationChatHistories.IgnoreQueryFilters()
                .Where(row => row.ConversationId == otherConversationId)
                .Select(row => row.TurnId)
                .SingleAsync(token)
        );
        Assert.Equal(
            [firstEvent, secondEvent, laterSegment],
            await context
                .DurableExecutionEvents.IgnoreQueryFilters()
                .Where(entry => entry.TurnId == executionId)
                .OrderBy(entry => entry.TurnSequence)
                .Select(entry => entry.Id)
                .ToListAsync(token)
        );
        Assert.Equal(
            [1L, 2L, 3L],
            await context
                .DurableExecutionEvents.IgnoreQueryFilters()
                .Where(entry => entry.TurnId == executionId)
                .OrderBy(entry => entry.TurnSequence)
                .Select(entry => entry.TurnSequence)
                .ToListAsync(token)
        );
        var connection = context.Database.GetDbConnection();
        Assert.Equal(
            3L,
            Convert.ToInt64(await ScalarAsync(connection, "SELECT last_event_sequence FROM durable_execution"))
        );
        Assert.False(context.Database.HasPendingModelChanges());

        await migrator.MigrateAsync(migrations[turnsMigration - 1], token);
        Assert.False(await SchemaColumnExistsAsync(connection, postgres, "project_conversation_turn", "id"));
        Assert.False(
            await SchemaColumnExistsAsync(connection, postgres, "project_conversation_chat_history", "history_scope")
        );
        Assert.True(await SchemaColumnExistsAsync(connection, postgres, "execution_stream_entry", "sequence"));
        var restored = await context
            .ProjectConversationChatHistories.IgnoreQueryFilters()
            .Where(row => row.Id == nodeInput)
            .Select(row => row.Metadata)
            .SingleAsync(token);
        Assert.Equal(scope, restored!["historyScope"].GetString());
    }

    private static string Target(string targetType, Guid targetId) =>
        JsonSerializer.Serialize(
            new Dictionary<string, string> { ["targetType"] = targetType, ["targetId"] = targetId.ToString("D") }
        );

    private static Task<int> InsertHistoryAsync(
        AgwDbContext context,
        bool postgres,
        Guid id,
        Guid conversationId,
        long sequence,
        ChatMessage message,
        string? metadata,
        DateTimeOffset createdAt
    )
    {
        var payload = JsonSerializer.Serialize(message, PayloadJsonOptions);
        return postgres
            ? context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO project_conversation_chat_history (id, project_conversation_id, task_id, status, conversation_sequence, conversation_payload, metadata, create_time) VALUES ({id}, {conversationId}, {conversationId}, 2, {sequence}, {payload}, {metadata}::jsonb, {createdAt})",
                TestContext.Current.CancellationToken
            )
            : context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO project_conversation_chat_history (id, project_conversation_id, task_id, status, conversation_sequence, conversation_payload, metadata, create_time) VALUES ({id}, {conversationId}, {conversationId}, 2, {sequence}, {payload}, {metadata}, {createdAt})",
                TestContext.Current.CancellationToken
            );
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> SchemaColumnExistsAsync(
        DbConnection connection,
        bool postgres,
        string table,
        string column
    ) =>
        Convert.ToInt64(
            await ScalarAsync(
                connection,
                postgres
                    ? $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table}' AND column_name = '{column}'"
                    : $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'"
            )
        ) == 1;
}
