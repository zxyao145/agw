using System.Security.Claims;
using Agw.Agents.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Events.Domain.Services;
using Agw.Events.Infrastructure;
using Agw.Extensions;
using Agw.Extensions.Contracts;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Events;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agw.Agents.Tests;

public sealed class BackgroundExecutionCompletionTransactionTests
{
    [Fact]
    public async Task RegisterCancelledAsync_StagesInterruptedRecordAndCompletionEventAtomically()
    {
        using var owner = PushUser("owner");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using var pending = new PendingTransportMessages();
        await using var staging = CreateEventStaging(db, pending);
        var transaction = new BackgroundExecutionCompletionTransaction(
            db,
            staging.EventStaging,
            InMemoryApplicationLock.Shared
        );
        var turnId = Guid.CreateVersion7();
        var tombstone = new BackgroundExecution
        {
            Id = turnId,
            CreateBy = "owner",
            CallerKey = "ext:11111111-1111-1111-1111-111111111111",
            CorrelationKey = "correlation-1",
            State = AgentExecutionState.Interrupted,
            Error = "cancelled before submit",
            CompletedAt = TimeProvider.System.GetUtcNow(),
            ChainId = turnId,
        };

        Assert.True(await transaction.RegisterCancelledAsync(tombstone, TestContext.Current.CancellationToken));
        Assert.False(
            await transaction.RegisterCancelledAsync(
                new BackgroundExecution
                {
                    Id = turnId,
                    CreateBy = "owner",
                    CallerKey = tombstone.CallerKey,
                    CorrelationKey = tombstone.CorrelationKey,
                    State = AgentExecutionState.Interrupted,
                    CompletedAt = tombstone.CompletedAt,
                    ChainId = turnId,
                },
                TestContext.Current.CancellationToken
            )
        );

        var persisted = await db
            .BackgroundExecutions.AsNoTracking()
            .SingleAsync(execution => execution.Id == turnId, TestContext.Current.CancellationToken);
        var completionEvent = await db
            .Events.AsNoTracking()
            .SingleAsync(record => record.CeId == $"{turnId:D}:completed", TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionState.Interrupted, persisted.State);
        Assert.Equal("agw.execution.completed", completionEvent.Type);
        Assert.Equal("/agw/executions", completionEvent.CeSource);
        Assert.Equal(turnId, completionEvent.ChainId);
        Assert.Single(await db.EventOutbox.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteAsync_UpdatesExecutionAndStagesOneCompletionEvent()
    {
        using var owner = PushUser("owner");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        await using var pending = new PendingTransportMessages();
        await using var staging = CreateEventStaging(db, pending);
        var transaction = new BackgroundExecutionCompletionTransaction(
            db,
            staging.EventStaging,
            InMemoryApplicationLock.Shared
        );
        var turnId = Guid.CreateVersion7();
        db.BackgroundExecutions.Add(
            new BackgroundExecution
            {
                Id = turnId,
                CreateBy = "owner",
                CallerKey = "delivery:22222222-2222-2222-2222-222222222222",
                CorrelationKey = "delivery:22222222-2222-2222-2222-222222222222",
                ProjectId = Guid.CreateVersion7(),
                ConversationId = Guid.CreateVersion7(),
                TargetKind = AgentTargetKind.Agent,
                TargetId = Guid.CreateVersion7(),
                State = AgentExecutionState.Running,
                ChainId = Guid.CreateVersion7(),
                Hops = 2,
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var completedAt = TimeProvider.System.GetUtcNow();

        var completion = new BackgroundExecutionCompletion
        {
            TurnId = turnId,
            OwnerUserId = "owner",
            State = AgentExecutionState.Completed,
            ResultText = "done",
            InputTokens = 11,
            OutputTokens = 7,
            CompletedAt = completedAt,
        };
        Assert.True(await transaction.CompleteAsync(completion, TestContext.Current.CancellationToken));
        Assert.False(await transaction.CompleteAsync(completion, TestContext.Current.CancellationToken));

        var persisted = await db
            .BackgroundExecutions.AsNoTracking()
            .SingleAsync(execution => execution.Id == turnId, TestContext.Current.CancellationToken);
        var completionEvent = await db
            .Events.AsNoTracking()
            .SingleAsync(record => record.CeId == $"{turnId:D}:completed", TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionState.Completed, persisted.State);
        Assert.Equal("done", persisted.ResultText);
        Assert.Equal(11, persisted.InputTokens);
        Assert.Equal(7, persisted.OutputTokens);
        Assert.Equal(2, completionEvent.Hops);
        Assert.Single(await db.EventOutbox.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private static AgwDbContext CreateDbContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options);

    private static StagingFixture CreateEventStaging(AgwDbContext db, PendingTransportMessages pending)
    {
        var options = Options.Create(new EventsOptions());
        var outbox = new EventOutboxStaging(db, options, pending, TimeProvider.System);
        var root = Path.Combine(AppContext.BaseDirectory, $"completion-extensions-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(AgwDataPaths.Resolve(Path.Combine(root, "data"), root, Path.Combine(root, "logs")));
        services.AddSingleton<IHostEnvironment>(_ =>
            Microsoft
                .Extensions.Hosting.Host.CreateApplicationBuilder(
                    new HostApplicationBuilderSettings { ContentRootPath = root, DisableDefaults = true }
                )
                .Environment
        );
        services.AddExtensions(new ConfigurationBuilder().Build(), "Standalone");
        var extensionServices = services.BuildServiceProvider();
        var extensions = extensionServices.GetRequiredService<IExtensionGenerationCatalog>();
        var sources = new EventPublisherSources(
            db,
            new EventIngestTransaction(db, pending, InMemoryApplicationLock.Shared),
            extensions,
            new EventSourceNameDomainService(new EventSourceNameRepository(db))
        );
        var eventStaging = new EventStaging(
            sources,
            new EventRecordStaging(db, outbox, new CloudEventEnvelopeSerializer(), options, TimeProvider.System),
            extensions,
            TimeProvider.System,
            extensionServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EventStaging>>()
        );
        return new StagingFixture(eventStaging, extensionServices, root);
    }

    private sealed class StagingFixture(EventStaging eventStaging, ServiceProvider extensionServices, string root)
        : IAsyncDisposable
    {
        public EventStaging EventStaging { get; } = eventStaging;

        public async ValueTask DisposeAsync()
        {
            await extensionServices.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "BackgroundExecutionTests")
            )
        );
}
