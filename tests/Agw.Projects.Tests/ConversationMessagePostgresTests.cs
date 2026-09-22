using System.Security.Claims;
using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.History;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.History;
using Agw.Shared.Configuration;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Agw.Projects.Tests;

public sealed class ConversationMessagePostgresTests
{
    public static bool Enabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact(
        SkipUnless = nameof(Enabled),
        Skip = "Requires an isolated PostgreSQL test database with CREATE DATABASE permission."
    )]
    public async Task UpsertSnapshots_Postgres_PreserveLegacyRowsAndMessageIdentity()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        var database = $"agw_message_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            await create.ExecuteNonQueryAsync(token);
        try
        {
            settings.Database = database;
            using var owner = UserInfoUtil.Push(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-owner")], "test"))
            );
            var options = new DbContextOptionsBuilder<AgwDbContext>();
            AgwDbContextOptionsConfigurator.Configure(options, DatabaseProvider.Postgres, settings.ConnectionString);
            await using var db = new AgwDbContext(options.Options);
            await db.Database.MigrateAsync(token);
            var projectId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            db.Projects.Add(
                new Project
                {
                    Id = projectId,
                    Name = "test",
                    CreateBy = "test-owner",
                }
            );
            db.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = conversationId,
                    ProjectId = projectId,
                    ContextId = "snapshot-test",
                    Title = "test",
                    CreateBy = "test-owner",
                }
            );
            await db.SaveChangesAsync(token);
            var legacyId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var legacyPayload = """{"role":"assistant"}""";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO project_conversation_chat_history (id, project_conversation_id, task_id, status, conversation_sequence, conversation_payload, create_time) VALUES ({legacyId}, {conversationId}, {legacyId}, 0, 0, {legacyPayload}, {now})",
                token
            );
            var legacy = await db
                .ProjectConversationChatHistories.AsNoTracking()
                .SingleAsync(row => row.Id == legacyId, token);
            Assert.Equal(legacyPayload, legacy.ConversationPayload);

            await using var services = new ServiceCollection()
                .AddScoped<IProjectsDbContext>(_ => new AgwDbContext(options.Options))
                .BuildServiceProvider();
            var writer = new EfCoreChatHistoryProvider(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EfCoreChatHistoryProvider>.Instance,
                TimeProvider.System
            );
            var scope = new ConversationMessageWriteScope
            {
                ProjectId = projectId,
                ContextId = "snapshot-test",
                Generation = 0,
                ProducerId = Guid.NewGuid(),
            };
            var id = Guid.NewGuid();
            ConversationMessageSnapshot Snapshot(string text) =>
                new()
                {
                    MessageId = id,
                    State = ConversationMessageState.Open,
                    CreatedAt = now,
                    Metadata = [],
                    Payload = JsonSerializer.Serialize(
                        new ChatMessage(ChatRole.Assistant, text) { MessageId = id.ToString("D") },
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    ),
                };
            await writer.UpsertAsync(scope, [Snapshot("a")], token);
            await writer.UpsertAsync(scope, [Snapshot("ab")], token);
            await writer.UpsertAsync(scope, [Snapshot("ab")], token);
            var row = await db.ProjectConversationChatHistories.AsNoTracking().SingleAsync(row => row.Id == id, token);
            Assert.Equal("ab", row.GetText());
            Assert.Equal(1, row.ConversationSequence);
            Assert.Equal(2, await db.ProjectConversationChatHistories.CountAsync(token));
            Assert.Equal(
                legacyPayload,
                (
                    await db
                        .ProjectConversationChatHistories.AsNoTracking()
                        .SingleAsync(row => row.Id == legacyId, token)
                ).ConversationPayload
            );
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
