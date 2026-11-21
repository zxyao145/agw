using System.Security.Claims;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Application.Persistence;
using Agw.Events.Infrastructure.Sources;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Testing;
using Agw.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventSourceScanTests
{
    [Fact]
    public async Task ScanHttpPoll_CloudEventsEmptyArray_SucceedsWithoutEvents()
    {
        using var owner = PushUser("owner");
        await using var fixture = await ScanFixture.CreateAsync("[]");

        await fixture.ScanAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var source = await db.EventSources.FindAsync([fixture.SourceId], TestContext.Current.CancellationToken);
        Assert.NotNull(source!.BaselineAt);
        Assert.Null(source.LastError);
        Assert.Empty(await db.EventSourceItems.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.Events.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScanHttpPoll_CloudEventsBaseline_RecordsItemsWithoutEvents()
    {
        using var owner = PushUser("owner");
        await using var fixture = await ScanFixture.CreateAsync(
            """[{"specversion":"1.0","id":"first","source":"/upstream","type":"demo.created"},{"specversion":"1.0","id":"second","source":"/upstream","type":"demo.updated"}]"""
        );

        await fixture.ScanAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Equal(2, await db.EventSourceItems.CountAsync(TestContext.Current.CancellationToken));
        Assert.Contains(
            await db.EventSourceItems.ToArrayAsync(TestContext.Current.CancellationToken),
            item => item.ItemKey == EventHttpPoller.CloudEventItemKey("/upstream", "first")
        );
        Assert.Empty(await db.Events.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScanHttpPoll_CloudEventsMixedBatch_StagesDeclaredAndDiscardsOthers()
    {
        using var owner = PushUser("owner");
        await using var fixture = await ScanFixture.CreateAsync("[]");
        await fixture.ScanAsync();
        fixture.Advance();
        fixture.Body =
            """[{"specversion":"1.0","id":"good","source":"/upstream","type":"demo.created"},{"specversion":"1.0","id":"other","source":"/upstream","type":"other.created"},{"specversion":"1.0","id":"invalid","source":"/upstream","type":"demo.created","agwforged":"value"}]""";

        await fixture.ScanAsync();

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            Assert.Equal(3, await db.EventSourceItems.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal("good", (await db.Events.SingleAsync(TestContext.Current.CancellationToken)).CeId);
            Assert.Null(
                (await db.EventSources.FindAsync([fixture.SourceId], TestContext.Current.CancellationToken))!.LastError
            );
        }

        fixture.Advance();
        await fixture.ScanAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            Assert.Equal(3, await db.EventSourceItems.CountAsync(TestContext.Current.CancellationToken));
            Assert.Single(await db.Events.ToArrayAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ScanHttpPoll_UnsupportedContentType_RecordsLastError()
    {
        using var owner = PushUser("owner");
        await using var fixture = await ScanFixture.CreateAsync("<html></html>", "text/html");

        await fixture.ScanAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.NotNull(
            (await db.EventSources.FindAsync([fixture.SourceId], TestContext.Current.CancellationToken))!.LastError
        );
        Assert.Empty(await db.EventSourceItems.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ScanHttpPoll_CloudEventsFixedType_ReplacesType()
    {
        using var owner = PushUser("owner");
        await using var fixture = await ScanFixture.CreateAsync("[]", fixedType: "demo.fixed");
        await fixture.ScanAsync();
        fixture.Advance();
        fixture.Body = """[{"specversion":"1.0","id":"one","source":"/upstream","type":"external.any"}]""";

        await fixture.ScanAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Equal("demo.fixed", (await db.Events.SingleAsync(TestContext.Current.CancellationToken)).Type);
    }

    [Fact]
    public async Task ScanHttpPoll_MappedDeclaredType_StagesAndAdvances()
    {
        using var owner = PushUser("owner");
        await using var fixture = await ScanFixture.CreateAsync("""{"items":[{"id":1}]}""", format: "mapped");
        await fixture.ScanAsync();
        fixture.Advance();
        fixture.Body = """{"items":[{"id":1},{"id":2}]}""";

        await fixture.ScanAsync();

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Equal(2, await db.EventSourceItems.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("demo.created", (await db.Events.SingleAsync(TestContext.Current.CancellationToken)).Type);
        Assert.Null(
            (await db.EventSources.FindAsync([fixture.SourceId], TestContext.Current.CancellationToken))!.LastError
        );
    }

    private static IDisposable PushUser(string id) =>
        UserInfoUtil.Push(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test")));

    private sealed class ScanFixture : IAsyncDisposable
    {
        private readonly WebApplication _server;
        private readonly ModuleCompositionTestHost _host;
        private readonly TestTimeProvider _timeProvider;
        private readonly ResponseState _response;

        private ScanFixture(
            WebApplication server,
            ModuleCompositionTestHost host,
            TestTimeProvider timeProvider,
            ResponseState response,
            Guid sourceId
        )
        {
            _server = server;
            _host = host;
            _timeProvider = timeProvider;
            _response = response;
            SourceId = sourceId;
        }

        public IServiceProvider Services => _host.Services;

        public Guid SourceId { get; }

        public string Body
        {
            get => _response.Body;
            set => _response.Body = value;
        }

        public static async Task<ScanFixture> CreateAsync(
            string body,
            string contentType = "application/cloudevents-batch+json",
            string? fixedType = null,
            string format = "cloudevents"
        )
        {
            var response = new ResponseState { Body = body, ContentType = contentType };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var server = builder.Build();
            server.MapGet(
                "/events",
                async (HttpContext context) =>
                {
                    context.Response.ContentType = response.ContentType;
                    await context.Response.WriteAsync(response.Body, context.RequestAborted);
                }
            );
            await server.StartAsync(TestContext.Current.CancellationToken);
            var address = server
                .Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
            var host = await ModuleCompositionTestHost.CreateAsync(
                settings: new Dictionary<string, string?> { ["Events:HttpPoll:AllowPrivateNetworks"] = "true" },
                configureServices: services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider))
            );
            var sourceId = Guid.CreateVersion7();
            object? mapping =
                format == "mapped"
                    ? new
                    {
                        items = "$.items[*]",
                        key = new Dictionary<string, string> { ["var"] = "item.id" },
                        change = new { kind = "content" },
                        @event = new { typeOnNew = "demo.created", typeOnChange = "demo.updated" },
                    }
                    : null;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                db.EventSources.Add(
                    new EventSource
                    {
                        Id = sourceId,
                        CreateBy = "owner",
                        Name = "http.test",
                        Kind = EventSourceKind.HttpPoll,
                        Enabled = true,
                        CeSource = $"/agw/sources/{sourceId:N}",
                        NextRunAt = timeProvider.GetUtcNow(),
                        ConfigJson = JsonSerializer.Serialize(
                            new
                            {
                                eventTypes = fixedType is null ? new[] { "demo.*" } : null,
                                fixedType,
                                request = new { url = $"{address}/events" },
                                interval = "PT5M",
                                format,
                                mapping,
                            }
                        ),
                    }
                );
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            return new ScanFixture(server, host, timeProvider, response, sourceId);
        }

        public void Advance() => _timeProvider.SetUtcNow(_timeProvider.GetUtcNow().AddMinutes(5));

        public async Task ScanAsync() =>
            await _host
                .Services.GetRequiredService<IEventSourceScanService>()
                .ScanDueAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            await _server.DisposeAsync();
        }

        private sealed class ResponseState
        {
            public required string Body { get; set; }

            public required string ContentType { get; init; }
        }
    }
}
