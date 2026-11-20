using System.Text.Json;
using Agw.Events.Application.Sources;
using Agw.Events.Configuration;
using Agw.Events.Infrastructure.Sources;
using Agw.Infrastructure.Events;
using Agw.Shared.Data.Entities.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Agw.Events.Tests;

public sealed class EventHttpPollerTests
{
    [Fact]
    public async Task Fetch_LinkHeaderPagination_ReturnsItemsFromEveryPage()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet(
            "/issues",
            async (HttpContext context) =>
            {
                if (context.Request.Query["page"] == "1")
                {
                    context.Response.Headers.Link = "</issues?page=2>; rel=\"next\"";
                    await context.Response.WriteAsJsonAsync(
                        new { items = new[] { new { id = 41 }, new { id = 42 } } },
                        context.RequestAborted
                    );
                    return;
                }

                await context.Response.WriteAsJsonAsync(
                    new { items = new[] { new { id = 43 } } },
                    context.RequestAborted
                );
            }
        );
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        var options = new EventHttpPollOptions { AllowPrivateNetworks = true };
        var services = new ServiceCollection();
        services
            .AddHttpClient(EventHttpPoller.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => EventHttpPoller.CreateHandler(options));
        using var provider = services.BuildServiceProvider();
        var poller = new EventHttpPoller(
            provider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(new EventsOptions { HttpPoll = options }),
            new CloudEventEnvelopeSerializer()
        );
        using var keyRule = JsonDocument.Parse("""{"var":"item.id"}""");
        var config = new EventHttpPollSourceConfig
        {
            EventTypes = ["issue.*"],
            Format = "mapped",
            Request = new EventHttpPollRequestConfig
            {
                Method = "GET",
                Url = $"{address}/issues?page=1",
                Auth = new EventHttpPollAuthConfig { Kind = "none" },
            },
            Pagination = new EventHttpPollPaginationConfig { Kind = "linkHeader", MaxPages = 3 },
            Mapping = new EventHttpPollMappingConfig
            {
                Items = "$.items[*]",
                Key = keyRule.RootElement.Clone(),
                Change = new EventHttpPollChangeConfig { Kind = "content" },
                Event = new EventHttpPollEventConfig { TypeOnNew = "issue.created", TypeOnChange = "issue.updated" },
            },
            Interval = "PT1M",
        };

        var items = await poller.FetchAsync(
            new EventSource { Id = Guid.CreateVersion7() },
            config,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new[] { "41", "42", "43" }, items.Select(item => item.Key));
    }

    [Theory]
    [InlineData("application/cloudevents-batch+json")]
    [InlineData("Application/CloudEvents-Batch+Json")]
    [InlineData("Application/JSON")]
    public async Task FetchCloudEvents_LinkHeaderPagination_ReturnsEventsFromEveryPage(string contentType)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet(
            "/events",
            async (HttpContext context) =>
            {
                var first = context.Request.Query["page"] == "1";
                if (first)
                    context.Response.Headers.Link = "</events?page=2>; rel=\"next\"";
                context.Response.ContentType = contentType;
                await context.Response.WriteAsync(
                    first
                        ? """[{"specversion":"1.0","id":"first","source":"/upstream","type":"demo.created"}]"""
                        : """[{"specversion":"1.0","id":"second","source":"/upstream","type":"demo.updated"}]""",
                    context.RequestAborted
                );
            }
        );
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        var options = new EventHttpPollOptions { AllowPrivateNetworks = true };
        var services = new ServiceCollection();
        services
            .AddHttpClient(EventHttpPoller.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(_ => EventHttpPoller.CreateHandler(options));
        using var provider = services.BuildServiceProvider();
        var poller = new EventHttpPoller(
            provider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(new EventsOptions { HttpPoll = options }),
            new CloudEventEnvelopeSerializer()
        );
        var config = new EventHttpPollSourceConfig
        {
            EventTypes = ["demo.*"],
            Interval = "PT1M",
            Request = new EventHttpPollRequestConfig { Url = $"{address}/events?page=1" },
            Pagination = new EventHttpPollPaginationConfig { Kind = "linkHeader", MaxPages = 2 },
        };

        var events = await poller.FetchCloudEventsAsync(
            new EventSource { Id = Guid.CreateVersion7() },
            config,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["first", "second"], events.Select(item => item.Id));
    }
}
