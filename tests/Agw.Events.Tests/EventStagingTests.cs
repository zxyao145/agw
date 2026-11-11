using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Events.Sources;
using Agw.Infrastructure.Events.Transport;
using Agw.Infrastructure.Notifications;
using Agw.Notifications.Application;
using Agw.Notifications.Application.Persistence;
using Agw.Notifications.Configuration;
using Agw.Notifications.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StageAsync_DuplicateEvent_PersistsOneEvent(bool outbox)
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(outbox);
        await using var scope = host.Services.CreateAsyncScope();
        var staging = scope.ServiceProvider.GetRequiredService<IEventStaging>();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var request = Request("event-1");

        var first = await staging.StageAsync(request, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var duplicate = await staging.StageAsync(request, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.EventId, duplicate.EventId);
        Assert.Equal(1, await db.Events.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(outbox ? 1 : 0, await db.EventOutbox.CountAsync(TestContext.Current.CancellationToken));
        if (!outbox)
        {
            var delivery = await host.ReadAcceptedAsync();
            Assert.Equal("event-1", delivery.Message.Id);
            Assert.Equal(first.EventId.ToString(), delivery.Message.Extensions["agweventid"].Value);
        }
    }

    [Fact]
    public async Task StageAsync_ExplicitTransaction_PublishesAfterCommit()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(false);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await scope
            .ServiceProvider.GetRequiredService<IEventStaging>()
            .StageAsync(Request("committed"), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(scope.ServiceProvider.GetRequiredService<PendingTransportMessages>().HasMessages);

        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        Assert.False(scope.ServiceProvider.GetRequiredService<PendingTransportMessages>().HasMessages);
        Assert.Equal("committed", (await host.ReadAcceptedAsync()).Message.Id);
    }

    [Fact]
    public async Task StageAsync_RolledBackTransaction_DiscardsPendingMessage()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(false);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            await scope
                .ServiceProvider.GetRequiredService<IEventStaging>()
                .StageAsync(Request("rollback"), TestContext.Current.CancellationToken);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }
        Assert.False(scope.ServiceProvider.GetRequiredService<PendingTransportMessages>().HasMessages);
        await using var verification = host.Services.CreateAsyncScope();
        Assert.Empty(
            await verification
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .Events.ToArrayAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task StageAsync_ParentEvent_InheritsChainAndControlsDepth()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var staging = scope.ServiceProvider.GetRequiredService<IEventStaging>();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var parent = await staging.StageAsync(Request("parent"), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var child = await staging.StageAsync(
            Request("child") with
            {
                Event = Request("child").Event with { ParentEventId = parent.EventId },
            },
            TestContext.Current.CancellationToken
        );
        var completion = await staging.StageAsync(
            new EventStagingRequest
            {
                PublisherKey = "agw.execution",
                InheritParentHops = true,
                Event = Request("completed").Event with
                {
                    Type = "agw.execution.completed",
                    ParentEventId = parent.EventId,
                },
            },
            TestContext.Current.CancellationToken
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            parent.EventId,
            (await db.Events.SingleAsync(row => row.Id == child.EventId, TestContext.Current.CancellationToken)).ChainId
        );
        Assert.Equal(
            1,
            (await db.Events.SingleAsync(row => row.Id == child.EventId, TestContext.Current.CancellationToken)).Hops
        );
        Assert.Equal(
            0,
            (
                await db.Events.SingleAsync(row => row.Id == completion.EventId, TestContext.Current.CancellationToken)
            ).Hops
        );
    }

    [Fact]
    public async Task StageAsync_ForeignParent_ReturnsNotFound()
    {
        await using var host = await StagingHost.CreateAsync(true);
        Guid parentId;
        using (PushUser("first"))
        {
            await using var scope = host.Services.CreateAsyncScope();
            parentId = (
                await scope
                    .ServiceProvider.GetRequiredService<IEventStaging>()
                    .StageAsync(Request("parent"), TestContext.Current.CancellationToken)
            ).EventId;
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using (PushUser("second"))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                scope
                    .ServiceProvider.GetRequiredService<IEventStaging>()
                    .StageAsync(
                        Request("child") with
                        {
                            Event = Request("child").Event with { ParentEventId = parentId },
                        },
                        TestContext.Current.CancellationToken
                    )
            );
            Assert.Equal(ErrorCodes.ResourceNotFound.Code, exception.Code);
        }
    }

    private static EventStagingRequest Request(string id)
    {
        const string json = "{\"value\":42}";
        return new EventStagingRequest
        {
            PublisherKey = "ext:019a4c17-0000-7000-8000-000000000001",
            Event = new CloudEventDraft
            {
                Id = id,
                Type = "example.updated",
                DataContentType = "application/json",
                Data = new CloudEventData
                {
                    Kind = CloudEventDataKind.Json,
                    Text = json,
                    Json = JsonSerializer.Deserialize<JsonElement>(json),
                },
            },
        };
    }

    private static IDisposable PushUser(string id) =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));

    private sealed class StagingHost : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ServiceProvider Services { get; }

        private StagingHost(SqliteConnection connection, ServiceProvider services)
        {
            _connection = connection;
            Services = services;
        }

        public static async Task<StagingHost> CreateAsync(bool outbox)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IApplicationLock>(new InMemoryApplicationLock());
            services.AddSingleton<IOptions<EventsOptions>>(
                Options.Create(new EventsOptions { Outbox = new EventOutboxOptions { Enabled = outbox } })
            );
            services.AddSingleton(new CloudEventEnvelopeSerializer());
            services.AddSingleton<IEventTransport, InProcessEventTransport>();
            services.AddScoped<PendingTransportMessages>();
            services.AddScoped<EventCommitCoordinator>();
            services.AddScoped<EventSaveChangesInterceptor>();
            services.AddScoped<EventTransactionInterceptor>();
            services.AddScoped<EventVersionInterceptor>();
            services.AddDbContext<AgwDbContext>(
                (provider, options) =>
                    options
                        .UseSqlite(connection)
                        .UseSnakeCaseNamingConvention()
                        .AddInterceptors(
                            provider.GetRequiredService<EventSaveChangesInterceptor>(),
                            provider.GetRequiredService<EventTransactionInterceptor>(),
                            provider.GetRequiredService<EventVersionInterceptor>()
                        )
            );
            services.AddScoped<IOutboxStaging, EventOutboxStaging>();
            services.AddScoped<EventRecordRecovery>();
            services.AddScoped<EventStaging>();
            services.AddScoped<IEventStaging>(provider => provider.GetRequiredService<EventStaging>());
            services.AddScoped<IEventContextFacade>(provider => provider.GetRequiredService<EventStaging>());
            services.AddScoped<IEventsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<INotificationsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
            services.AddScoped<INotificationTransaction, NotificationTransaction>();
            services.AddScoped<NotificationAppService>();
            services.AddScoped<INotificationStaging, NotificationStagingFacade>();
            services.AddOptions<NotificationDispatchOptions>();
            services.AddScoped<IEventIngestTransaction, EventIngestTransaction>();
            services.AddScoped<IEventScheduledPublisher, EventScheduledPublisher>();
            services.AddScoped<INotificationDispatchStore, NotificationDispatchStore>();
            services.AddScoped<IEventDispatchNoticeStaging, EventDispatchNoticeStaging>();
            services.AddScoped<EventScheduleRunner>();
            services.AddScoped<EventDispatcher>();
            services.AddScoped<IEventWebhookReceiver, EventWebhookReceiver>();
            var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new StagingHost(connection, provider);
        }

        public async Task<TransportDelivery> ReadAcceptedAsync()
        {
            return await ReadTransportDeliveryAsync(EventMessages.Accepted);
        }

        public async Task<TransportDelivery> ReadTransportDeliveryAsync(string topic)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider.System);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                TestContext.Current.CancellationToken
            );
            await using var enumerator = Services
                .GetRequiredService<IEventTransport>()
                .ConsumeAsync(topic, "test", cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
            Assert.True(await enumerator.MoveNextAsync());
            return enumerator.Current;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
