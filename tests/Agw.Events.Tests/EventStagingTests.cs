using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Fact]
    public async Task StageAsync_BuiltInSourceMissing_CreatesManagedRow()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var staging = scope.ServiceProvider.GetRequiredService<IEventStaging>();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();

        var result = await staging.StageAsync(
            new EventStagingRequest
            {
                BuiltInSource = EventBuiltInSources.Execution,
                Event = new CloudEventDraft { Id = "completion-1", Type = "agw.execution.completed" },
            },
            TestContext.Current.CancellationToken
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var source = await db.EventSources.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EventBuiltInSources.Execution, source.Name);
        Assert.Equal(EventSourceKind.Publisher, source.Kind);
        Assert.Equal(EventManagedBy.Agw, source.ManagedBy);
        Assert.Equal(EventBuiltInSources.Execution, source.ManagedKey);
        Assert.Equal("/agw/executions", source.CeSource);
        Assert.Equal(source.Id, (await db.Events.SingleAsync(TestContext.Current.CancellationToken)).SourceId);
        Assert.False(result.Duplicate);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task StageAsync_BothOrNeitherPublisher_ThrowsInvalidParam(bool builtIn, bool extension)
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var request = new EventStagingRequest
        {
            BuiltInSource = builtIn ? EventBuiltInSources.Execution : null,
            ExtensionId = extension ? Guid.CreateVersion7() : null,
            Event = new CloudEventDraft { Id = "invalid", Type = "agw.execution.completed" },
        };

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IEventStaging>()
                .StageAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public async Task EnsureBuiltInAsync_CalledConcurrently_CreatesOneRow()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);

        async Task<Guid> EnsureAsync()
        {
            await using var scope = host.Services.CreateAsyncScope();
            var source = await scope
                .ServiceProvider.GetRequiredService<IEventPublisherSources>()
                .EnsureBuiltInAsync(EventBuiltInSources.Execution, TestContext.Current.CancellationToken);
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
            return source.Id;
        }

        var ids = await Task.WhenAll(EnsureAsync(), EnsureAsync());

        Assert.Equal(ids[0], ids[1]);
        await using var verification = host.Services.CreateAsyncScope();
        Assert.Equal(
            1,
            await verification
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .EventSources.CountAsync(TestContext.Current.CancellationToken)
        );
    }

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
    public async Task StageExternalAsync_MappedDraftOutsideDeclaration_RecordsDiscard()
    {
        using var user = PushUser("owner");
        await using var host = await StagingHost.CreateAsync(true);
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var token = TestContext.Current.CancellationToken;
        var created = await service.CreateAsync(
            new EventSourceCreateCommand
            {
                Name = "http.orders",
                Kind = EventSourceKind.HttpPoll,
                Config = JsonSerializer.Deserialize<JsonElement>(
                    """
                    {"eventTypes":["demo.*"],"request":{"url":"https://example.test/orders"},"interval":"PT5M",
                     "format":"mapped","mapping":{"items":"$.items[*]","key":{"var":"item.id"},
                       "change":{"kind":"content"},"event":{"typeOnNew":"demo.created","typeOnChange":"demo.updated"}}}
                    """
                ),
            },
            token
        );
        var source = await db.EventSources.SingleAsync(row => row.Id == created.Id, token);
        var staging = scope.ServiceProvider.GetRequiredService<EventStaging>();

        var accepted = await staging.StageExternalAsync(
            source,
            new CloudEventDraft { Id = "one", Type = "demo.created" },
            token
        );
        var discarded = await staging.StageExternalAsync(
            source,
            new CloudEventDraft { Id = "two", Type = "other.created" },
            token
        );
        await db.SaveChangesAsync(token);

        Assert.True(discarded.Discarded);
        Assert.Null(discarded.EventId);
        Assert.Equal(accepted.EventId, (await db.Events.SingleAsync(token)).Id);
        Assert.Equal(accepted.EventId, (await db.EventOutbox.SingleAsync(token)).EntityId);
        Assert.Equal("other.created", source.LastDiscardedType);
        Assert.NotNull(source.LastDiscardedAt);
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
                BuiltInSource = EventBuiltInSources.Execution,
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
            BuiltInSource = EventBuiltInSources.Notifications,
            Event = new CloudEventDraft
            {
                Id = id,
                Type = "agw.notification.answered",
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
        private readonly ModuleCompositionTestHost _host;

        private StagingHost(ModuleCompositionTestHost host) => _host = host;

        public IServiceProvider Services => _host.Services;

        public static async Task<StagingHost> CreateAsync(bool outbox, TimeProvider? timeProvider = null) =>
            new(
                await ModuleCompositionTestHost.CreateAsync(
                    outboxEnabled: outbox,
                    configureServices: timeProvider is null
                        ? null
                        : services => services.Replace(ServiceDescriptor.Singleton(timeProvider))
                )
            );

        public Task<TransportDelivery> ReadAcceptedAsync() => ReadTransportDeliveryAsync(EventMessages.Accepted);

        public Task<TransportDelivery> ReadTransportDeliveryAsync(string topic) =>
            ModuleCompositionTestHost.ReadTransportDeliveryAsync(Services.GetRequiredService<IEventTransport>(), topic);

        public ValueTask DisposeAsync() => _host.DisposeAsync();
    }
}
