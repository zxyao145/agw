using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Events.Application.Persistence;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agw.Infrastructure.Events;

public sealed partial class EventStagingTests
{
    [Fact]
    public async Task ReceiveAsync_MappedHmacWebhook_AuthenticatesAndDeduplicates()
    {
        await using var host = await StagingHost.CreateAsync(true);
        var id = await AddWebhookAsync(host, "mapped");
        const string body = "{\"title\":\"Issue updated\"}";
        Guid eventId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope
                .ServiceProvider.GetRequiredService<IEventWebhookReceiver>()
                .ReceiveAsync(id, Webhook(body), TestContext.Current.CancellationToken);
            eventId = result.EventId;
            Assert.False(result.Duplicate);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope
                .ServiceProvider.GetRequiredService<IEventWebhookReceiver>()
                .ReceiveAsync(id, Webhook(body), TestContext.Current.CancellationToken);
            Assert.True(result.Duplicate);
            Assert.Equal(eventId, result.EventId);
        }
        using var owner = PushUser("owner");
        await using var verification = host.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AgwDbContext>();
        var record = await db.Events.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("example.updated", record.Type);
        Assert.Equal("delivery-1", record.CeId);
        Assert.Equal("owner", record.CreateBy);
        Assert.Equal(body, record.CeData);
        Assert.Single(await db.EventOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(
        "{\"specversion\":\"1.0\",\"id\":\"id\",\"source\":\"/external\",\"type\":\"agw.execution.completed\"}",
        false
    )]
    [InlineData(
        "{\"specversion\":\"1.0\",\"id\":\"id\",\"source\":\"/external\",\"type\":\"example.updated\",\"agwforged\":\"value\"}",
        true
    )]
    public async Task ReceiveAsync_StructuredWebhook_RejectsPlatformIdentity(string body, bool attribute)
    {
        await using var host = await StagingHost.CreateAsync(true);
        var id = await AddWebhookAsync(host, "cloudevents");
        await using var scope = host.Services.CreateAsyncScope();
        var context = Webhook(body, "application/cloudevents+json");
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IEventWebhookReceiver>()
                .ReceiveAsync(id, context, TestContext.Current.CancellationToken)
        );
        Assert.Equal(attribute ? ErrorCodes.InvalidParam.Code : ErrorCodes.EventTypeReserved.Code, error.Code);
        using var owner = PushUser("owner");
        Assert.Empty(
            await scope
                .ServiceProvider.GetRequiredService<AgwDbContext>()
                .Events.ToArrayAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task ReceiveAsync_BinaryCloudEvent_PreservesOriginalSourceAndJsonData()
    {
        await using var host = await StagingHost.CreateAsync(true);
        var id = await AddWebhookAsync(host, "cloudevents");
        var context = Webhook("{\"count\":3}");
        context.Request.Headers["ce-specversion"] = "1.0";
        context.Request.Headers["ce-id"] = "upstream-event";
        context.Request.Headers["ce-source"] = CloudNative.CloudEvents.Http.HttpUtilities.EncodeHeaderValue(
            "https://example.test/a%2Fb"
        );
        context.Request.Headers["ce-type"] = "example.updated";
        await using var scope = host.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<IEventWebhookReceiver>()
            .ReceiveAsync(id, context, TestContext.Current.CancellationToken);
        using var owner = PushUser("owner");
        var record = await scope
            .ServiceProvider.GetRequiredService<AgwDbContext>()
            .Events.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("https://example.test/a%2Fb", record.CeSource);
        Assert.Equal("{\"count\":3}", record.CeData);
    }

    [Fact]
    public async Task ReceiveAsync_InvalidSignature_PersistsNoEvent()
    {
        await using var host = await StagingHost.CreateAsync(true);
        var id = await AddWebhookAsync(host, "mapped");
        var context = Webhook("{}");
        context.Request.Headers["X-Hub-Signature-256"] = "sha256=" + new string('0', 64);
        await using var scope = host.Services.CreateAsyncScope();
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope
                .ServiceProvider.GetRequiredService<IEventWebhookReceiver>()
                .ReceiveAsync(id, context, TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCodes.WebhookAuthenticationFailed.Code, error.Code);
    }

    private static async Task<Guid> AddWebhookAsync(StagingHost host, string format)
    {
        using var owner = PushUser("owner");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var id = Guid.CreateVersion7();
        db.EventSources.Add(
            new EventSource
            {
                Id = id,
                CreateBy = "owner",
                Name = "Webhook",
                Kind = EventSourceKind.Webhook,
                Enabled = true,
                CeSource = $"/agw/sources/{id}",
                SecretJson = JsonSerializer.Serialize("webhook-secret"),
                ConfigJson = JsonSerializer.Serialize(
                    new
                    {
                        format,
                        auth = new { kind = "HmacSha256" },
                        contentTypes = new[] { "application/json", "application/cloudevents+json" },
                        mapping = new Dictionary<string, object>
                        {
                            ["id"] = new Dictionary<string, string> { ["var"] = "headers.X-GitHub-Delivery" },
                            ["type"] = "example.updated",
                        },
                    }
                ),
            }
        );
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private static DefaultHttpContext Webhook(string body, string contentType = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(bytes, writable: false);
        context.Request.Headers["x-github-delivery"] = "delivery-1";
        context.Request.Headers["X-Hub-Signature-256"] =
            "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-secret"), bytes));
        return context;
    }
}
