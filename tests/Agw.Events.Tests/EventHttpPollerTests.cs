using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Infrastructure.Events.Sources;
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
            Options.Create(new EventsOptions { HttpPoll = options })
        );
        using var keyRule = JsonDocument.Parse("""{"var":"item.id"}""");
        var config = new EventHttpPollSourceConfig
        {
            Request = new EventHttpPollRequestConfig
            {
                Method = "GET",
                Url = $"{address}/issues?page=1",
                Auth = new EventHttpPollAuthConfig { Kind = "none" },
            },
            Pagination = new EventHttpPollPaginationConfig { Kind = "linkHeader", MaxPages = 3 },
            Items = "$.items[*]",
            Key = keyRule.RootElement.Clone(),
            Change = new EventHttpPollChangeConfig { Kind = "content" },
            Event = new EventHttpPollEventConfig { TypeOnNew = "issue.created", TypeOnChange = "issue.updated" },
            Interval = "PT1M",
        };

        var items = await poller.FetchAsync(
            new EventSource { Id = Guid.CreateVersion7() },
            config,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new[] { "41", "42", "43" }, items.Select(item => item.Key));
    }
}
