using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Configuration;
using Agw.Agents.Execution.Messaging.Durable;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Infrastructure.Data;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    public static bool BatchPostgresEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING"));

    [Fact(
        SkipUnless = nameof(BatchPostgresEnabled),
        Skip = "Requires an isolated PostgreSQL instance with CREATE DATABASE permission."
    )]
    public async Task PostgresBatch_ConcurrentOverlappingBatches_KeepEveryPositionExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var name = $"agw_event_batch_{Guid.NewGuid():N}";
        var settings = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("AGW_TEST_POSTGRES_CONNECTION_STRING")
        )
        {
            Pooling = false,
        };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync(token);
        try
        {
            settings.Database = name;
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseNpgsql(settings.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var db = new AgwDbContext(options);
            await db.Database.EnsureCreatedAsync(token);
            var task = CreateTask();
            db.Projects.Add(new Project { Id = task.ProjectId, CreateBy = "user-id" });
            db.ProjectConversations.Add(
                new ProjectConversation
                {
                    Id = task.ProjectConversationId,
                    ProjectId = task.ProjectId,
                    ContextId = task.ContextId,
                    CreateBy = "user-id",
                }
            );
            await db.SaveChangesAsync(token);
            var store = new DurableExecutionStore(
                db,
                TimeProvider.System,
                InMemoryApplicationLock.Shared,
                TestDurablePersistence.Create(db)
            );
            var id = Guid.NewGuid();
            await store.RegisterAsync(
                id,
                "user-id",
                Guid.NewGuid(),
                AgentRuntimeType.Agent,
                CreateInput("postgres batch"),
                task,
                CreateSettings(task.ProjectId, task.ContextId),
                token
            );
            await using var services = new ServiceCollection()
                .AddScoped<IAgentsDbContext>(_ => new AgwDbContext(options))
                .BuildServiceProvider();
            var stream = new PostgresExecutionEventStream(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ExecutionRuntimeOptions())
            );
            var first = BatchText("first position is immutable");
            await stream.AppendAsync(id, 0, 0, first, token);

            await Task.WhenAll(
                Enumerable
                    .Range(0, 8)
                    .Select(batch =>
                        stream
                            .AppendBatchAsync(
                                id,
                                0,
                                Enumerable
                                    .Range(batch * 5, 20)
                                    .Select(sequence => new ExecutionStreamWrite(
                                        sequence,
                                        BatchText($"{batch}:{sequence}")
                                    ))
                                    .ToArray(),
                                token
                            )
                            .AsTask()
                    )
            );

            var records = await stream.ReadAsync(id, null, token);
            Assert.Equal(55, records.Count);
            Assert.Equal(
                Enumerable.Range(0, 55).Select(sequence => $"1-{sequence}"),
                records.Select(record => record.Cursor)
            );
            Assert.Equal(first.MessageId, records[0].Message.MessageId);
            var resumed = await stream.ReadAsync(id, "1-24", token);
            Assert.Equal(30, resumed.Count);
            Assert.Equal("1-25", resumed[0].Cursor);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
