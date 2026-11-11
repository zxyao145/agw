using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Interceptors;
using Agw.Infrastructure.Events;
using Agw.Infrastructure.Events.Transport;
using Agw.Infrastructure.Notifications;
using Agw.Notifications.Application.Persistence;
using Agw.Notifications.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using EntityDispatchAttemptResult = Agw.Shared.Data.Entities.Notifications.NotificationDispatchAttemptResult;
using EntityDispatchState = Agw.Shared.Data.Entities.Notifications.NotificationDispatchState;
using EntityNotificationKind = Agw.Shared.Data.Entities.Notifications.NotificationKind;

namespace Agw.Notifications.Tests;

public sealed class NotificationRestartRecoveryTests
{
    private const string DispatchTopic = "agw.notifications.dispatch";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_SendingDispatch_ReplaysAndAllowsLeaseTakeover(bool outboxEnabled)
    {
        var now = new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        var databasePath = CreateDatabasePath();
        using var owner = PushUser("owner-1");
        Guid dispatchId;
        Guid firstAttemptId;

        try
        {
            await using (var host = await RestartHost.CreateAsync(databasePath, outboxEnabled, timeProvider))
            {
                dispatchId = await SeedPendingAsync(host, "restart-sending", now);
                if (outboxEnabled)
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                        .RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
                }

                var delivery = await ReadDispatchAsync(host.Services.GetRequiredService<IEventTransport>());
                Assert.Equal($"{dispatchId:D}:1", delivery.Message.Id);
                await using var claimScope = host.Services.CreateAsyncScope();
                var claim = await claimScope
                    .ServiceProvider.GetRequiredService<INotificationDispatchStore>()
                    .TryClaimAsync(dispatchId, "host-a", TestContext.Current.CancellationToken);
                Assert.NotNull(claim);
                firstAttemptId = claim.AttemptId;
            }

            timeProvider.SetUtcNow(now.AddMinutes(2));
            await using (var host = await RestartHost.CreateAsync(databasePath, outboxEnabled, timeProvider))
            {
                await using (var recoveryScope = host.Services.CreateAsyncScope())
                {
                    var db = recoveryScope.ServiceProvider.GetRequiredService<AgwDbContext>();
                    var stored = await db
                        .NotificationDispatches.AsNoTracking()
                        .SingleAsync(item => item.Id == dispatchId, TestContext.Current.CancellationToken);
                    Assert.Equal(EntityDispatchState.Sending, stored.State);
                    Assert.True(
                        await recoveryScope
                            .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                            .RecoverAsync(true, TestContext.Current.CancellationToken) > 0
                    );
                    if (outboxEnabled)
                    {
                        await recoveryScope
                            .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                            .RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
                    }
                }

                var replay = await ReadDispatchAsync(host.Services.GetRequiredService<IEventTransport>());
                Assert.Equal($"{dispatchId:D}:1", replay.Message.Id);
                await using var takeoverScope = host.Services.CreateAsyncScope();
                var store = takeoverScope.ServiceProvider.GetRequiredService<INotificationDispatchStore>();
                var takeover = await store.TryClaimAsync(dispatchId, "host-b", TestContext.Current.CancellationToken);

                Assert.NotNull(takeover);
                Assert.NotEqual(firstAttemptId, takeover.AttemptId);
                Assert.Equal(2, takeover.Dispatch.Attempt);
                Assert.True(
                    await store.CompleteAsync(
                        dispatchId,
                        takeover.AttemptId,
                        "host-b",
                        takeover.Dispatch.MessageVersion,
                        EntityDispatchAttemptResult.Sent,
                        "external-message-2",
                        error: null,
                        TestContext.Current.CancellationToken
                    )
                );
                Assert.False(
                    await store.CompleteAsync(
                        dispatchId,
                        firstAttemptId,
                        "host-a",
                        takeover.Dispatch.MessageVersion,
                        EntityDispatchAttemptResult.Sent,
                        "stale-external-message",
                        error: null,
                        TestContext.Current.CancellationToken
                    )
                );
                var completed = await takeoverScope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .NotificationDispatches.AsNoTracking()
                    .SingleAsync(item => item.Id == dispatchId, TestContext.Current.CancellationToken);
                var attempts = await takeoverScope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .NotificationDispatchAttempts.AsNoTracking()
                    .Where(item => item.DispatchId == dispatchId)
                    .OrderBy(item => item.Attempt)
                    .ToListAsync(TestContext.Current.CancellationToken);

                Assert.Equal(EntityDispatchState.Sent, completed.State);
                Assert.Equal("external-message-2", completed.ExternalMessageId);
                Assert.Equal(2, attempts.Count);
                Assert.Equal(EntityDispatchAttemptResult.Sending, attempts[0].Result);
                Assert.Equal(EntityDispatchAttemptResult.Sent, attempts[1].Result);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_DueFailedDispatch_RequeuesAndPublishes(bool outboxEnabled)
    {
        var now = new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        var databasePath = CreateDatabasePath();
        using var owner = PushUser("owner-1");
        Guid dispatchId;

        try
        {
            await using (var host = await RestartHost.CreateAsync(databasePath, outboxEnabled, timeProvider))
            {
                dispatchId = await SeedPendingAsync(host, "restart-failed", now);
                if (outboxEnabled)
                {
                    await using var scope = host.Services.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                        .RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
                }

                var initialDelivery = await ReadDispatchAsync(host.Services.GetRequiredService<IEventTransport>());
                Assert.Equal($"{dispatchId:D}:1", initialDelivery.Message.Id);
                await using var failureScope = host.Services.CreateAsyncScope();
                var store = failureScope.ServiceProvider.GetRequiredService<INotificationDispatchStore>();
                var claim = await store.TryClaimAsync(dispatchId, "host-a", TestContext.Current.CancellationToken);
                Assert.NotNull(claim);
                Assert.True(
                    await store.CompleteAsync(
                        dispatchId,
                        claim.AttemptId,
                        "host-a",
                        claim.Dispatch.MessageVersion,
                        EntityDispatchAttemptResult.Failed,
                        externalMessageId: null,
                        error: "Temporary channel failure.",
                        TestContext.Current.CancellationToken
                    )
                );
                await failureScope
                    .ServiceProvider.GetRequiredService<IOutboxStaging>()
                    .CompleteAsync(DispatchTopic, dispatchId, TestContext.Current.CancellationToken);
                await failureScope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            timeProvider.SetUtcNow(now.AddSeconds(31));
            await using (var host = await RestartHost.CreateAsync(databasePath, outboxEnabled, timeProvider))
            {
                await using (var recoveryScope = host.Services.CreateAsyncScope())
                {
                    var db = recoveryScope.ServiceProvider.GetRequiredService<AgwDbContext>();
                    var stored = await db
                        .NotificationDispatches.AsNoTracking()
                        .SingleAsync(item => item.Id == dispatchId, TestContext.Current.CancellationToken);
                    Assert.Equal(EntityDispatchState.Failed, stored.State);
                    Assert.Equal(
                        1,
                        await recoveryScope
                            .ServiceProvider.GetRequiredService<INotificationDispatchStore>()
                            .RecoverDueAsync(TestContext.Current.CancellationToken)
                    );
                    if (outboxEnabled)
                    {
                        await recoveryScope
                            .ServiceProvider.GetRequiredService<EventRecordRecovery>()
                            .RecoverDueRetriesAsync(TestContext.Current.CancellationToken);
                    }
                }

                var retry = await ReadDispatchAsync(host.Services.GetRequiredService<IEventTransport>());
                Assert.Equal($"{dispatchId:D}:1", retry.Message.Id);
                await using var retryScope = host.Services.CreateAsyncScope();
                var claim = await retryScope
                    .ServiceProvider.GetRequiredService<INotificationDispatchStore>()
                    .TryClaimAsync(dispatchId, "host-b", TestContext.Current.CancellationToken);

                Assert.NotNull(claim);
                Assert.Equal(2, claim.Dispatch.Attempt);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static async Task<Guid> SeedPendingAsync(RestartHost host, string key, DateTimeOffset now)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var notification = NotificationAppServiceTests.NewEntityNotification(key, EntityNotificationKind.Notice);
        var extensionId = Guid.CreateVersion7();
        var dispatch = NotificationAppServiceTests.NewDispatch(notification.Id, $"ext:{extensionId:D}:cards:target-1");
        dispatch.RecoverAfter = now.AddSeconds(-1);
        await scope
            .ServiceProvider.GetRequiredService<INotificationTransaction>()
            .CreateAsync(notification, question: null, [dispatch], TestContext.Current.CancellationToken);
        return dispatch.Id;
    }

    private static async Task<TransportDelivery> ReadDispatchAsync(IEventTransport transport)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            TestContext.Current.CancellationToken
        );
        await using var enumerator = transport
            .ConsumeAsync(DispatchTopic, "restart-test", cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static IDisposable PushUser(string userId) =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "restart-test"))
        );

    private static string CreateDatabasePath()
    {
        var directory = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".scratch", "notifications-recovery")
        );
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.CreateVersion7():D}.db");
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var file in new[] { path, $"{path}-shm", $"{path}-wal" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private sealed class RestartHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private RestartHost(ServiceProvider services)
        {
            _services = services;
        }

        public ServiceProvider Services => _services;

        public static async Task<RestartHost> CreateAsync(
            string databasePath,
            bool outboxEnabled,
            TestTimeProvider timeProvider
        )
        {
            var eventsOptions = new EventsOptions
            {
                Transport = new EventTransportOptions { Provider = "InProcess" },
                Outbox = new EventOutboxOptions
                {
                    Enabled = outboxEnabled,
                    StaleAfter = TimeSpan.FromSeconds(5),
                    RecoveryInterval = TimeSpan.FromSeconds(1),
                },
            };
            var dispatchOptions = new NotificationDispatchOptions
            {
                ClaimDuration = TimeSpan.FromMinutes(1),
                BaseBackoff = TimeSpan.FromSeconds(30),
                MaxBackoff = TimeSpan.FromMinutes(1),
                RecoveryStaleAfter = TimeSpan.FromMinutes(5),
                RecoveryInterval = TimeSpan.FromSeconds(1),
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddSingleton<IApplicationLock>(new InMemoryApplicationLock());
            services.AddSingleton<IOptions<EventsOptions>>(Options.Create(eventsOptions));
            services.AddSingleton<IOptions<NotificationDispatchOptions>>(Options.Create(dispatchOptions));
            services.AddSingleton(new CloudEventEnvelopeSerializer());
            services.AddSingleton<IEventTransport, InProcessEventTransport>();
            services.AddSingleton<Agw.Shared.Data.Abstractions.IEntityAuditUserIdProvider>(
                new NotificationTestFixture.TestAuditUserIdProvider()
            );
            services.AddScoped<EntityCreatorInterceptor>();
            services.AddScoped<EntityModifierInterceptor>();
            services.AddScoped<EventCommitCoordinator>();
            services.AddScoped<EventSaveChangesInterceptor>();
            services.AddScoped<EventTransactionInterceptor>();
            services.AddScoped<EventVersionInterceptor>();
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true,
                Pooling = false,
            }.ToString();
            services.AddDbContext<AgwDbContext>(
                (provider, options) =>
                    options
                        .UseSqlite(connectionString)
                        .UseSnakeCaseNamingConvention()
                        .AddInterceptors(
                            provider.GetRequiredService<EntityCreatorInterceptor>(),
                            provider.GetRequiredService<EntityModifierInterceptor>(),
                            provider.GetRequiredService<EventSaveChangesInterceptor>(),
                            provider.GetRequiredService<EventTransactionInterceptor>(),
                            provider.GetRequiredService<EventVersionInterceptor>()
                        )
            );
            services.AddScoped<PendingTransportMessages>();
            services.AddScoped<IOutboxStaging, EventOutboxStaging>();
            services.AddScoped<EventStaging>();
            services.AddScoped<IEventStaging>(provider => provider.GetRequiredService<EventStaging>());
            services.AddScoped<IEventsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<INotificationsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<INotificationTransaction, NotificationTransaction>();
            services.AddScoped<INotificationDispatchStore, NotificationDispatchStore>();
            services.AddScoped<IEventIngestTransaction, EventIngestTransaction>();
            services.AddScoped<EventRecordRecovery>();

            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
                await scope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new RestartHost(provider);
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }
}
