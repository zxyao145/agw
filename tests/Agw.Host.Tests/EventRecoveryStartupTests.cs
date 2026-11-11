using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Auth.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Xunit;
using ServerProcess = Agw.Host.Tests.ServerStartupTests.ServerProcess;

namespace Agw.Host.Tests;

public sealed class EventRecoveryStartupTests
{
    private const string CheckpointError = "Event dispatch interrupted at the recovery checkpoint";
    private const string WebhookSecret = "event-recovery-test-webhook";
    private const string WebhookBody = """
        {"specversion":"1.0","id":"process-restart","source":"/test","type":"example.updated","data":{"count":3}}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryScan_DeliveryBecomesDue_ConsumesBeforeStaleRecovery(bool outboxEnabled)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", $"event-retry-{Guid.CreateVersion7():N}");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(root);
        try
        {
            using var server = ServerProcess.Start(
                root,
                AppContext.BaseDirectory,
                [
                    "serve",
                    $"--AgwDataDir={data}",
                    $"--Setup:AdminPassword={Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))}",
                    $"--Extensions:Directory={Path.Combine(root, "extensions")}",
                    "--Events:Transport:Provider=InProcess",
                    $"--Events:Outbox:Enabled={outboxEnabled}",
                    "--Events:Outbox:RecoveryInterval=00:00:30",
                ]
            );
            await server.WaitUntilReadyAsync(Path.Combine(data, "runtime", "server.json"));
            var database = Path.Combine(data, "database", "agw.db");
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite($"Data Source={database};Pooling=False")
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var db = new AgwDbContext(options);
            using var systemScope = UserInfoUtil.PushSystemScope();
            var deliveryId = await SeedRetryAsync(db);

            await WaitUntilAsync(
                () =>
                    db.EventDeliveries.AnyAsync(
                        row => row.Id == deliveryId && row.State == EventDeliveryState.Cancelled,
                        CancellationToken
                    ),
                server
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Guid> SeedRetryAsync(AgwDbContext db)
    {
        const string owner = "event-retry-owner";
        var now = TimeProvider.System.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var deliveryId = Guid.CreateVersion7();
        db.Events.Add(
            new EventRecord
            {
                Id = eventId,
                SourceId = Guid.CreateVersion7(),
                CeId = eventId.ToString("D"),
                CeSource = "/test/retry",
                Type = "example.retry",
                DispatchState = EventDispatchState.Dispatched,
                CreateBy = owner,
                CreateTime = now,
                ReceivedAt = now,
                ChainId = eventId,
                RecoverAfter = now.AddMinutes(5),
            }
        );
        db.EventDeliveries.Add(
            new EventDelivery
            {
                Id = deliveryId,
                EventId = eventId,
                SubscriptionId = Guid.CreateVersion7(),
                State = EventDeliveryState.RetryScheduled,
                Attempt = 1,
                NextAttemptAt = now.AddSeconds(2),
                RecoverAfter = now.AddMinutes(5),
                CreateBy = owner,
                CreateTime = now,
            }
        );
        await db.SaveChangesAsync(CancellationToken);
        return deliveryId;
    }

    [Theory]
    [InlineData("InProcess", false, Checkpoint.DispatchCommit)]
    [InlineData("InProcess", true, Checkpoint.DispatchCommit)]
    [InlineData("Redis", false, Checkpoint.Publication)]
    [InlineData("Redis", true, Checkpoint.Publication)]
    [InlineData("Redis", false, Checkpoint.Consumption)]
    [InlineData("Redis", true, Checkpoint.Consumption)]
    [InlineData("Redis", false, Checkpoint.Acknowledgement)]
    [InlineData("Redis", true, Checkpoint.Acknowledgement)]
    public async Task ReceiveWebhook_ProcessTerminatedAtCheckpoint_RecoversAcceptedEvent(
        string transport,
        bool outboxEnabled,
        Checkpoint checkpoint
    )
    {
        var redis = transport == "Redis";
        var redisConnection = redis ? IsolatedTestConnections.Redis : null;
        Assert.SkipWhen(redis && redisConnection is null, "Configure .scratch/redis-tests.json to run the Redis case.");
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", $"event-recovery-{Guid.CreateVersion7():N}");
        var data = Path.Combine(root, "data");
        var descriptor = Path.Combine(data, "runtime", "server.json");
        var hostIdentity = Path.Combine(data, "runtime", "extension-host-Standalone.json");
        var database = Path.Combine(data, "database", "agw.db");
        var adminPassword = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        using var redisClient = redisConnection is null
            ? null
            : await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var broker = redisClient?.GetDatabase();
        string? streamPrefix = null;
        Directory.CreateDirectory(root);
        try
        {
            Guid sourceId;
            Guid eventId;
            var firstConnection = checkpoint switch
            {
                Checkpoint.Publication => UnavailableRedisAddress(),
                Checkpoint.Consumption => DisableRedisCommand(redisConnection!, "XREADGROUP"),
                Checkpoint.Acknowledgement => DisableRedisCommand(redisConnection!, "XACK"),
                _ => redisConnection,
            };
            using (var first = Start(firstConnection))
            {
                await first.WaitUntilReadyAsync(descriptor);
                using var client = first.CreateClient();
                if (broker is not null)
                {
                    using var serverInfo = await client.GetAsync("/api/server-info", CancellationToken);
                    streamPrefix = $"agw:{(await ReadDataAsync(serverInfo)).GetProperty("serverId").GetString()}:";
                }
                sourceId = await CreateWebhookAsync(client);
                if (checkpoint == Checkpoint.DispatchCommit)
                    await InterruptDispatchCommitAsync(database);

                eventId = await ReceiveWebhookAsync(client, sourceId);
                if (checkpoint is Checkpoint.Consumption or Checkpoint.Acknowledgement)
                {
                    var stream = streamPrefix + EventTopics.Accepted;
                    await WaitUntilAsync(async () => await broker!.StreamLengthAsync(stream) > 0, first);
                    if (checkpoint == Checkpoint.Acknowledgement)
                    {
                        await WaitUntilAsync(
                            async () => (await broker!.StreamPendingAsync(stream, "agw")).PendingMessageCount > 0,
                            first
                        );
                        using var identity = JsonDocument.Parse(
                            await File.ReadAllTextAsync(hostIdentity, CancellationToken)
                        );
                        var pending = await broker!.StreamPendingMessagesAsync(stream, "agw", 1, RedisValue.Null);
                        Assert.Equal(
                            identity.RootElement.GetProperty("HostId").GetString(),
                            Assert.Single(pending).ConsumerName.ToString()
                        );
                    }
                }
                else
                {
                    var failure = (checkpoint, outboxEnabled) switch
                    {
                        (Checkpoint.Publication, true) => "Event outbox publication failed",
                        (Checkpoint.Publication, false) => "Event transport publish failed after commit",
                        _ => CheckpointError,
                    };
                    await WaitUntilAsync(
                        () => Task.FromResult(first.Output().Contains(failure, StringComparison.Ordinal)),
                        first
                    );
                }

                if (checkpoint == Checkpoint.Acknowledgement)
                    await WaitUntilAsync(
                        async () => await ReadDispatchStateAsync(client, eventId) == EventDispatchState.NoMatch,
                        first
                    );
                else
                    Assert.Equal(EventDispatchState.Pending, await ReadDispatchStateAsync(client, eventId));
                await first.KillAsync();
                Assert.NotEqual(0, first.ExitCode);
            }

            if (checkpoint == Checkpoint.DispatchCommit)
                await ExecuteSqlAsync(database, "DROP TRIGGER interrupt_event_dispatch;");

            using var restarted = Start(redisConnection);
            await restarted.WaitUntilReadyAsync(descriptor);
            using var recoveredClient = restarted.CreateClient();
            await WaitUntilAsync(
                async () => await ReadDispatchStateAsync(recoveredClient, eventId) == EventDispatchState.NoMatch,
                restarted
            );
            if (broker is not null)
            {
                await WaitUntilAsync(
                    async () =>
                        (
                            await broker.StreamPendingAsync(streamPrefix + EventTopics.Accepted, "agw")
                        ).PendingMessageCount == 0,
                    restarted
                );
            }
            Assert.Equal(eventId, await ReceiveWebhookAsync(recoveredClient, sourceId, forwarded: true));
            using var invalid = CreateWebhookRequest(sourceId, forwarded: true);
            invalid.Headers.Remove("X-Hub-Signature-256");
            using var rejection = await recoveredClient.SendAsync(invalid, CancellationToken);
            Assert.Equal(ErrorCodes.WebhookAuthenticationFailed.StatusCode, rejection.StatusCode);
            using var error = JsonDocument.Parse(await rejection.Content.ReadAsStringAsync(CancellationToken));
            Assert.Equal(ErrorCodes.WebhookAuthenticationFailed.Code, error.RootElement.GetProperty("code").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (broker is not null && streamPrefix is not null)
            {
                await broker.KeyDeleteAsync([
                    streamPrefix + EventTopics.Accepted,
                    streamPrefix + EventTopics.Delivery,
                    streamPrefix + EventTopics.NotificationDispatch,
                ]);
            }
        }

        ServerProcess Start(string? connection) =>
            ServerProcess.Start(
                root,
                AppContext.BaseDirectory,
                [
                    "serve",
                    $"--AgwDataDir={data}",
                    $"--Setup:AdminPassword={adminPassword}",
                    $"--Extensions:Directory={Path.Combine(root, "extensions")}",
                    $"--Events:Transport:Provider={transport}",
                    $"--Events:Transport:Redis:ConnectionString={connection}",
                    $"--Events:Outbox:Enabled={outboxEnabled}",
                    "--Events:Outbox:StaleAfter=00:00:01",
                    "--Events:Outbox:RecoveryInterval=00:00:00.200",
                ]
            );
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static async Task<Guid> CreateWebhookAsync(HttpClient client)
    {
        using var antiforgery = await client.GetAsync("/api/auth/antiforgery", CancellationToken);
        var token = (await ReadDataAsync(antiforgery)).GetProperty("requestToken").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/events/sources/create");
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Content = JsonContent.Create(
            new
            {
                name = "Process restart webhook",
                kind = EventSourceKind.Webhook,
                enabled = true,
                config = new
                {
                    format = "cloudevents",
                    auth = new { kind = "HmacSha256" },
                    contentTypes = new[] { "application/cloudevents+json" },
                },
                secret = WebhookSecret,
            }
        );
        using var response = await client.SendAsync(request, CancellationToken);
        return (await ReadDataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ReceiveWebhookAsync(HttpClient client, Guid sourceId, bool forwarded = false)
    {
        using var request = CreateWebhookRequest(sourceId, forwarded);
        using var response = await client.SendAsync(request, CancellationToken);
        var data = await ReadDataAsync(response);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return data.GetProperty("eventId").GetGuid();
    }

    private static HttpRequestMessage CreateWebhookRequest(Guid sourceId, bool forwarded)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{EventWebhookRoute.Receive}?sourceId={sourceId:D}")
        {
            Content = new StringContent(WebhookBody, Encoding.UTF8, "application/cloudevents+json"),
        };
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes(WebhookBody));
        request.Headers.Add("X-Hub-Signature-256", $"sha256={Convert.ToHexStringLower(signature)}");
        if (forwarded)
            request.Headers.Add("Forwarded", "for=192.0.2.1");
        return request;
    }

    private static async Task<EventDispatchState> ReadDispatchStateAsync(HttpClient client, Guid eventId)
    {
        using var response = await client.GetAsync($"/api/events/get?id={eventId:D}", CancellationToken);
        return (EventDispatchState)(await ReadDataAsync(response)).GetProperty("dispatchState").GetInt32();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, ServerProcess server)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(TimeProvider.System.GetUtcNow() < deadline, server.Output());
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
        }
    }

    private static string UnavailableRedisAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $"127.0.0.1:{port},connectTimeout=250,asyncTimeout=1000,syncTimeout=1000,connectRetry=0";
    }

    private static string DisableRedisCommand(string connection, string command)
    {
        var options = ConfigurationOptions.Parse(connection);
        options.CommandMap = CommandMap.Create([command], available: false);
        return options.ToString(includePassword: true);
    }

    private static Task InterruptDispatchCommitAsync(string database) =>
        ExecuteSqlAsync(
            database,
            $"""
            CREATE TRIGGER interrupt_event_dispatch BEFORE UPDATE OF dispatch_state ON event
            WHEN OLD.dispatch_state <> NEW.dispatch_state
            BEGIN SELECT RAISE(ABORT, '{CheckpointError}'); END;
            """
        );

    private static async Task ExecuteSqlAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync(CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    public enum Checkpoint
    {
        Publication,
        Consumption,
        DispatchCommit,
        Acknowledgement,
    }
}
