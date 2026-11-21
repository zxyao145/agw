using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Events.Tests;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Events;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Events.Application;

public sealed class EventManagementListTests
{
    [Fact]
    public async Task ListEventsAsync_SecondPage_ReturnsOrderedItemsAndTotal()
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = TimeProvider.System.GetUtcNow();
        var ids = Enumerable.Range(0, 25).Select(_ => Guid.CreateVersion7()).OrderDescending().ToArray();
        db.Events.AddRange(
            ids.Select(
                    (id, index) =>
                        NewEvent(
                            id,
                            "paging.test",
                            Guid.CreateVersion7(),
                            now.AddMinutes(index is >= 8 and <= 13 ? -8 : -index)
                        )
                )
                .Reverse()
        );
        db.Events.AddRange(
            Enumerable.Range(0, 3).Select(_ => NewEvent(Guid.CreateVersion7(), "other", Guid.CreateVersion7(), now))
        );
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();
        var filter = new EventListFilter { Type = "paging.test" };
        var first = await service.ListEventsAsync(filter, 1, 10, ct);
        var second = await service.ListEventsAsync(filter, 2, 10, ct);
        Assert.Equal(25, second.Total);
        Assert.Equal(2, second.PageIndex);
        Assert.Equal(10, second.PageSize);
        Assert.Equal(ids[10..20], second.Items.Select(item => item.Id));
        Assert.Equal(ids[..20], first.Items.Concat(second.Items).Select(item => item.Id));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task ListEventsAsync_PagePastEnd_ReturnsEmptyItemsAndActualTotal(int pageIndex)
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = TimeProvider.System.GetUtcNow();
        var chainId = Guid.CreateVersion7();
        db.Events.AddRange(
            Enumerable.Range(0, 25).Select(_ => NewEvent(Guid.CreateVersion7(), "paging.test", chainId, now))
        );
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();
        var page = await service.ListEventsAsync(new EventListFilter { Type = "paging.test" }, pageIndex, 10, ct);
        Assert.Empty(page.Items);
        Assert.Equal(25, page.Total);
        Assert.Equal(pageIndex, page.PageIndex);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(501, 500)]
    public async Task ListChainAsync_ChainEvents_ReturnsChainEventsNewestFirst(int chainSize, int expectedSize)
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = TimeProvider.System.GetUtcNow();
        var chainId = Guid.CreateVersion7();
        var chain = Enumerable
            .Range(0, chainSize)
            .Select(index => NewEvent(Guid.CreateVersion7(), "paging.test", chainId, now.AddMinutes(index)))
            .ToArray();
        db.Events.AddRange(chain);
        db.Events.AddRange(
            Enumerable
                .Range(0, 2)
                .Select(_ => NewEvent(Guid.CreateVersion7(), "paging.test", Guid.CreateVersion7(), now))
        );
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();
        var items = await service.ListChainAsync(chainId, ct);
        Assert.Equal(chain.Reverse().Take(expectedSize).Select(row => row.Id), items.Select(row => row.Id));
    }

    [Fact]
    public async Task ListSubscriptionsAsync_SameNames_PagesWithoutOverlap()
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(0, 25).Select(_ => Guid.CreateVersion7()).Order().ToArray();
        db.EventSubscriptions.AddRange(
            ids.Reverse().Select(id => new EventSubscription { Id = id, Name = "Same name" })
        );
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();
        var pages = new List<Guid>();
        for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
        {
            var page = await service.ListSubscriptionsAsync(pageIndex, 10, ct);
            Assert.Equal(25, page.Total);
            Assert.Equal(pageIndex == 3 ? 5 : 10, page.Items.Count);
            pages.AddRange(page.Items.Select(row => row.Id));
        }
        Assert.Equal(ids, pages);
    }

    [Fact]
    public async Task ListDeliveriesAsync_EventFilter_PagesFilteredDeliveries()
    {
        using var user = PushOwner();
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var eventId = Guid.CreateVersion7();
        var otherEventId = Guid.CreateVersion7();
        var pending = NewDeliveries(12, eventId, EventDeliveryState.Pending);
        db.EventDeliveries.AddRange(pending);
        db.EventDeliveries.AddRange(NewDeliveries(3, eventId, EventDeliveryState.Succeeded));
        db.EventDeliveries.AddRange(NewDeliveries(5, otherEventId, EventDeliveryState.Pending));
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();
        var page = await service.ListDeliveriesAsync(eventId, EventDeliveryState.Pending, 2, 10, ct);
        Assert.Equal(12, page.Total);
        Assert.Equal(pending.Select(row => row.Id).OrderDescending().Skip(10), page.Items.Select(row => row.Id));
        Assert.All(
            page.Items,
            row =>
            {
                Assert.Equal(eventId, row.EventId);
                Assert.Equal(EventDeliveryState.Pending, row.State);
            }
        );
    }

    [Fact]
    public async Task ListSchedulesAsync_NullDueAt_SortsLast()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        await NullDueAtScenarioAsync(host, TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task ListSchedulesAsync_NullDueAtOnPostgres_SortsLast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PostgresEventTestDatabase.CreateAsync(cancellationToken);
        await using var host = await ModuleCompositionTestHost.CreateAsync(settings: database.HostSettings);
        await NullDueAtScenarioAsync(host, cancellationToken);
    }

    private static async Task NullDueAtScenarioAsync(
        ModuleCompositionTestHost host,
        CancellationToken cancellationToken
    )
    {
        using var user = PushOwner();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var now = TimeProvider.System.GetUtcNow();
        var dated = new[] { NewSchedule(), NewSchedule(), NewSchedule() }.OrderByDescending(row => row.Id).ToArray();
        for (var index = 0; index < dated.Length; index++)
        {
            dated[index].DueAt = now.AddMinutes(index);
        }

        var undated = new[] { NewSchedule(), NewSchedule() }.OrderBy(row => row.Id).ToArray();
        db.EventSchedules.AddRange(undated[1], dated[2], dated[0], undated[0], dated[1]);
        await db.SaveChangesAsync(cancellationToken);

        var service = scope.ServiceProvider.GetRequiredService<EventManagementAppService>();
        var page = await service.ListSchedulesAsync(1, 10, cancellationToken);
        Assert.Equal(5, page.Total);
        Guid[] expected = [dated[0].Id, dated[1].Id, dated[2].Id, undated[0].Id, undated[1].Id];
        Assert.Equal(expected, page.Items.Select(row => row.Id));
    }

    private static EventRecord NewEvent(Guid id, string type, Guid chainId, DateTimeOffset receivedAt) =>
        new()
        {
            Id = id,
            CeId = id.ToString("N"),
            CeSource = "/paging",
            Type = type,
            ChainId = chainId,
            ReceivedAt = receivedAt,
        };

    private static EventDelivery[] NewDeliveries(int count, Guid eventId, EventDeliveryState state) =>
        Enumerable
            .Range(0, count)
            .Select(_ => new EventDelivery
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                SubscriptionId = Guid.CreateVersion7(),
                State = state,
            })
            .ToArray();

    private static EventSchedule NewSchedule() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            CallerKey = "paging",
            BusinessKey = Guid.NewGuid().ToString("N"),
        };

    private static IDisposable PushOwner() =>
        UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"))
        );
}
