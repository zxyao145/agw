using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Events;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Events.Application.Sources;

public sealed class EventSourceListTests
{
    [Fact]
    public async Task ListAsync_SourcePages_ReturnUserSourcesWithoutOverlap()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        using (PushUser("other"))
        {
            await using var otherScope = host.Services.CreateAsyncScope();
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            otherDb.EventSources.AddRange(Enumerable.Range(0, 3).Select(_ => NewSource("Alpha")));
            await otherDb.SaveChangesAsync(ct);
        }

        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var alpha = Enumerable.Range(0, 12).Select(_ => NewSource("Alpha")).OrderBy(row => row.Id).ToArray();
        var beta = Enumerable.Range(0, 11).Select(_ => NewSource("Beta")).OrderBy(row => row.Id).ToArray();
        var deletedAt = TimeProvider.System.GetUtcNow();
        db.EventSources.AddRange(beta.Reverse().Concat(alpha.Reverse()));
        db.EventSources.AddRange(NewSource("Alpha", deletedAt), NewSource("Beta", deletedAt));
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var ids = new List<Guid>();
        for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
        {
            var page = await service.ListAsync(pageIndex, 10, ct);
            Assert.Equal(23, page.Total);
            Assert.Equal(pageIndex == 3 ? 3 : 10, page.Items.Count);
            ids.AddRange(page.Items.Select(row => row.Id));
        }
        Assert.Equal(alpha.Concat(beta).Select(row => row.Id), ids);
    }

    [Fact]
    public async Task ListOptionsAsync_ManySources_ReturnsAllSources()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        using (PushUser("other"))
        {
            await using var otherScope = host.Services.CreateAsyncScope();
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            otherDb.EventSources.Add(NewSource("Other user's source"));
            await otherDb.SaveChangesAsync(ct);
        }

        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var sources = Enumerable
            .Range(0, 101)
            .Select(_ => NewSource(string.Empty))
            .OrderByDescending(row => row.Id)
            .ToArray();
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index].Name = $"Source {index:D3}";
        }
        db.EventSources.AddRange(sources.Reverse());
        db.EventSources.Add(NewSource("Deleted", TimeProvider.System.GetUtcNow()));
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var options = await service.ListOptionsAsync(ct);
        Assert.Equal(sources.Select(row => (row.Id, row.Name)), options.Select(option => (option.Id, option.Name)));
    }

    private static EventSource NewSource(string name, DateTimeOffset? deletedAt = null)
    {
        var id = Guid.CreateVersion7();
        return new EventSource
        {
            Id = id,
            Name = name,
            CeSource = $"/agw/sources/{id:N}",
            DeletedAt = deletedAt,
        };
    }

    private static IDisposable PushUser(string id) =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));
}
