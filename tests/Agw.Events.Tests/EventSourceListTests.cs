using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Events.Application.Sources;

public sealed class EventSourceListTests
{
    [Fact]
    public async Task CreateAsync_NameUsedByActiveSource_ThrowsNameConflict()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var request = TimerSource("timer.daily");
        await service.CreateAsync(request, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.CreateAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.EventSourceNameConflict.Code, error.Code);
    }

    [Fact]
    public async Task ListOptionsAsync_NewUser_ReturnsBuiltInSourcesWithoutDuplicates()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();

        var first = await service.ListOptionsAsync(TestContext.Current.CancellationToken);
        var second = await service.ListOptionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Count);
        Assert.Equal(first.Select(source => source.Id), second.Select(source => source.Id));
        Assert.Contains(
            first,
            source =>
                source.Name == EventBuiltInSources.Execution
                && source.ManagedBy == EventManagedBy.Agw
                && source.EventTypes.SequenceEqual(["agw.execution.completed"])
        );
        Assert.Contains(
            first,
            source => source.Name == EventBuiltInSources.Notifications && source.Kind == EventSourceKind.Publisher
        );
    }

    [Fact]
    public async Task ListAsync_WebhookWithFixedType_ReturnsDeclaredType()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        await service.CreateAsync(WebhookSource(), TestContext.Current.CancellationToken);

        var page = await service.ListAsync(1, 20, TestContext.Current.CancellationToken);

        var webhook = Assert.Single(page.Items, source => source.Kind == EventSourceKind.Webhook);
        Assert.Equal(["demo.ticket.created"], service.GetEventTypes(webhook));
    }

    [Fact]
    public async Task ListAsync_DiscardedEvent_ReturnsLastDiscarded()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var source = NewSource("timer.daily");
        db.EventSources.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var result = await scope
            .ServiceProvider.GetRequiredService<EventStaging>()
            .StageExternalAsync(
                source,
                new CloudEventDraft { Id = "outside", Type = "other.created" },
                TestContext.Current.CancellationToken
            );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var page = await service.ListAsync(1, 20, TestContext.Current.CancellationToken);

        Assert.True(result.Discarded);
        var listed = Assert.Single(page.Items, item => item.Id == source.Id);
        Assert.Equal("other.created", listed.LastDiscardedType);
        Assert.NotNull(listed.LastDiscardedAt);
    }

    [Fact]
    public async Task ListOptionsAsync_InactiveExtensionSources_ReturnsSourcesWithoutDeclaredTypes()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var extensionId = Guid.CreateVersion7();
        var publisher = NewSource("ext.publisher");
        publisher.Kind = EventSourceKind.Publisher;
        publisher.ManagedBy = EventManagedBy.ForExtension(extensionId);
        var provider = NewSource("ext.provider");
        provider.Kind = EventSourceKind.Provider;
        provider.ProviderExtensionId = extensionId;
        provider.ProviderId = "provider";
        provider.ConfigJson = "{}";
        db.EventSources.AddRange(publisher, provider);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();

        var options = await service.ListOptionsAsync(TestContext.Current.CancellationToken);
        var page = await service.ListAsync(1, 20, TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(options, option => option.Id == publisher.Id).EventTypes);
        Assert.Empty(Assert.Single(options, option => option.Id == provider.Id).EventTypes);
        Assert.Empty(service.GetEventTypes(Assert.Single(page.Items, source => source.Id == publisher.Id)));
        Assert.Empty(service.GetEventTypes(Assert.Single(page.Items, source => source.Id == provider.Id)));
    }

    [Fact]
    public async Task CreateAsync_DeletedName_CanBeReused()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var first = await service.CreateAsync(TimerSource("timer.daily"), TestContext.Current.CancellationToken);
        await service.DeleteAsync(
            new EventSourceVersionCommand { Id = first.Id, Version = first.Version },
            TestContext.Current.CancellationToken
        );

        var second = await service.CreateAsync(TimerSource("timer.daily"), TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task CreateAsync_PrefixDoesNotMatchKind_ThrowsInvalidParam()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope
                .ServiceProvider.GetRequiredService<EventSourceAppService>()
                .CreateAsync(TimerSource("http.daily"), TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public async Task UpdateAsync_RenameToUsedName_ThrowsNameConflict()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        await service.CreateAsync(TimerSource("timer.daily"), TestContext.Current.CancellationToken);
        var second = await service.CreateAsync(TimerSource("timer.weekly"), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            service.UpdateAsync(
                new EventSourceUpdateCommand
                {
                    Id = second.Id,
                    Version = second.Version,
                    Name = "timer.daily",
                    Config = TimerConfig(),
                },
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.EventSourceNameConflict.Code, error.Code);
    }

    [Fact]
    public async Task CreateAsync_WebhookWithSecret_ThrowsInvalidParam()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var request = WebhookSource() with { Secret = JsonSerializer.SerializeToElement("provided") };

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope
                .ServiceProvider.GetRequiredService<EventSourceAppService>()
                .CreateAsync(request, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public async Task CreateAsync_Webhook_ReturnsGeneratedSecretAndExpandedConfig()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();

        var result = await scope
            .ServiceProvider.GetRequiredService<EventSourceAppService>()
            .CreateAsync(WebhookSource(), TestContext.Current.CancellationToken);

        Assert.Matches("^[A-Za-z0-9_-]{43}$", result.Secret);
        var source = await scope
            .ServiceProvider.GetRequiredService<AgwDbContext>()
            .EventSources.FindAsync([result.Id], TestContext.Current.CancellationToken);
        Assert.NotNull(source);
        Assert.Equal(JsonSerializer.Serialize(result.Secret), source.SecretJson);
        using var config = JsonDocument.Parse(source.ConfigJson);
        Assert.Equal("cloudevents", config.RootElement.GetProperty("format").GetString());
        Assert.Equal("BearerToken", config.RootElement.GetProperty("auth").GetProperty("kind").GetString());
        Assert.Equal("demo.ticket.created", config.RootElement.GetProperty("fixedType").GetString());
    }

    [Fact]
    public async Task ListAsync_SourcePages_ReturnUserSourcesWithoutOverlap()
    {
        await using var host = await ModuleCompositionTestHost.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        using (PushUser("other"))
        {
            await using var otherScope = host.Services.CreateAsyncScope();
            var otherDb = otherScope.ServiceProvider.GetRequiredService<AgwDbContext>();
            otherDb.EventSources.AddRange(Enumerable.Range(0, 3).Select(index => NewSource($"Alpha-{index:D2}")));
            await otherDb.SaveChangesAsync(ct);
        }

        using var user = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var alpha = Enumerable.Range(0, 12).Select(index => NewSource($"Alpha-{index:D2}")).ToArray();
        var beta = Enumerable.Range(0, 11).Select(index => NewSource($"Beta-{index:D2}")).ToArray();
        var deletedAt = TimeProvider.System.GetUtcNow();
        db.EventSources.AddRange(beta.Reverse().Concat(alpha.Reverse()));
        db.EventSources.AddRange(NewSource("Alpha-00", deletedAt), NewSource("Beta-00", deletedAt));
        await db.SaveChangesAsync(ct);

        var service = scope.ServiceProvider.GetRequiredService<EventSourceAppService>();
        var ids = new List<Guid>();
        for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
        {
            var page = await service.ListAsync(pageIndex, 10, ct);
            Assert.Equal(25, page.Total);
            Assert.Equal(pageIndex == 3 ? 5 : 10, page.Items.Count);
            ids.AddRange(page.Items.Select(row => row.Id));
        }
        Assert.Equal(alpha.Concat(beta).Select(row => row.Id), ids.Take(23));
        Assert.Equal(25, ids.Count);
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
        Assert.Equal(
            sources.Select(row => (row.Id, row.Name)),
            options.Where(option => option.Kind == EventSourceKind.Timer).Select(option => (option.Id, option.Name))
        );
        Assert.Equal(103, options.Count);
    }

    private static EventSource NewSource(string name, DateTimeOffset? deletedAt = null)
    {
        var id = Guid.CreateVersion7();
        return new EventSource
        {
            Id = id,
            Name = name,
            Kind = EventSourceKind.Timer,
            CeSource = $"/agw/sources/{id:N}",
            ConfigJson = TimerConfig().GetRawText(),
            DeletedAt = deletedAt,
        };
    }

    private static EventSourceCreateCommand TimerSource(string name) =>
        new()
        {
            Name = name,
            Kind = EventSourceKind.Timer,
            Config = TimerConfig(),
            Enabled = false,
        };

    private static EventSourceCreateCommand WebhookSource() =>
        new()
        {
            Name = "webhook.tickets",
            Kind = EventSourceKind.Webhook,
            Config = JsonSerializer.SerializeToElement(new { fixedType = "demo.ticket.created" }),
        };

    private static JsonElement TimerConfig() =>
        JsonSerializer.SerializeToElement(
            new
            {
                kind = "Once",
                at = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
                timeZone = "UTC",
                @event = new { type = "demo.timer" },
            }
        );

    private static IDisposable PushUser(string id) =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));
}
