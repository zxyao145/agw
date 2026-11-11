using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Agw.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace Agw.Infrastructure.Events.Transport;

public sealed class RedisEventTransportTests : IClassFixture<RedisServerFixture>
{
    private readonly RedisServerFixture _redis;

    public RedisEventTransportTests(RedisServerFixture redis)
    {
        _redis = redis;
    }

    [Theory]
    [InlineData(CloudEventDataKind.Json)]
    [InlineData(CloudEventDataKind.Text)]
    [InlineData(CloudEventDataKind.Binary)]
    public async Task PublishAsync_CloudEventDataKinds_RoundTrip(CloudEventDataKind dataKind)
    {
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        var expected = CreateMessage("message-1", dataKind);

        var messageId = await transport.PublishAsync("events.test", expected, TestContext.Current.CancellationToken);
        var delivery = await ReadOneAsync(transport, "events.test", "consumer-a");

        Assert.Equal(messageId, delivery.MessageId);
        Assert.Equal("events.test", delivery.Topic);
        Assert.Equal(1, delivery.DeliveryCount);
        AssertCloudEventEqual(expected, delivery.Message);
    }

    [Fact]
    public async Task PublishAsync_DifferentServersAndTopics_UsesIsolatedStreams()
    {
        var serverId = Guid.NewGuid().ToString("N");
        var firstServer = CreateTransport(serverId);
        var secondServer = CreateTransport(Guid.NewGuid().ToString("N"));
        var firstMessage = CreateMessage("first");
        var secondMessage = CreateMessage("second");
        var thirdMessage = CreateMessage("third");

        await firstServer.PublishAsync("events.first", firstMessage, TestContext.Current.CancellationToken);
        await secondServer.PublishAsync("events.first", secondMessage, TestContext.Current.CancellationToken);
        await firstServer.PublishAsync("events.second", thirdMessage, TestContext.Current.CancellationToken);

        AssertCloudEventEqual(firstMessage, (await ReadOneAsync(firstServer, "events.first", "consumer")).Message);
        AssertCloudEventEqual(secondMessage, (await ReadOneAsync(secondServer, "events.first", "consumer")).Message);
        AssertCloudEventEqual(thirdMessage, (await ReadOneAsync(firstServer, "events.second", "consumer")).Message);
    }

    [Fact]
    public async Task ConsumeAsync_TwoConsumers_DistributesEachPublishedMessageOnce()
    {
        const int messageCount = 150;
        const string topic = "events.concurrent";
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        for (var index = 0; index < messageCount; index++)
        {
            await transport.PublishAsync(
                topic,
                CreateMessage($"message-{index}"),
                TestContext.Current.CancellationToken
            );
        }

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var messageIds = new ConcurrentBag<string>();
        var receivedCount = 0;

        async Task ConsumeUntilCompleteAsync(string consumer)
        {
            try
            {
                await foreach (
                    var delivery in transport
                        .ConsumeAsync(topic, consumer, cancellationTokenSource.Token)
                        .WithCancellation(cancellationTokenSource.Token)
                )
                {
                    messageIds.Add(delivery.MessageId);
                    if (Interlocked.Increment(ref receivedCount) == messageCount)
                    {
                        cancellationTokenSource.Cancel();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
            {
                if (Volatile.Read(ref receivedCount) < messageCount)
                {
                    throw;
                }
            }
        }

        await Task.WhenAll(ConsumeUntilCompleteAsync("consumer-a"), ConsumeUntilCompleteAsync("consumer-b"));

        Assert.Equal(messageCount, messageIds.Count);
        Assert.Equal(messageCount, messageIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ConsumeAsync_Cancellation_StopsPolling()
    {
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = transport
            .ConsumeAsync("events.empty", "consumer", cancellationTokenSource.Token)
            .GetAsyncEnumerator(cancellationTokenSource.Token);

        var moveNext = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
    }

    [Fact]
    public async Task ClaimStaleAsync_PendingMessageSurvivesNewTransportInstance()
    {
        var serverId = Guid.NewGuid().ToString("N");
        var originalTransport = CreateTransport(serverId);
        var expected = CreateMessage("pending-message");
        var messageId = await originalTransport.PublishAsync(
            "events.pending",
            expected,
            TestContext.Current.CancellationToken
        );
        await ReadOneAsync(originalTransport, "events.pending", "consumer-old");

        var restartedTransport = CreateTransport(serverId);
        var claimed = await ReadAllAsync(
            restartedTransport.ClaimStaleAsync(
                "events.pending",
                "consumer-new",
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            ),
            TestContext.Current.CancellationToken
        );

        var delivery = Assert.Single(claimed);
        Assert.Equal(messageId, delivery.MessageId);
        Assert.Equal("events.pending", delivery.Topic);
        AssertCloudEventEqual(expected, delivery.Message);
    }

    [Fact]
    public async Task AckAsync_AcknowledgedMessage_IsNotClaimedAsStale()
    {
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        await transport.PublishAsync("events.ack", CreateMessage("ack-message"), TestContext.Current.CancellationToken);
        var delivery = await ReadOneAsync(transport, "events.ack", "consumer");

        await transport.AckAsync(delivery, TestContext.Current.CancellationToken);
        var claimed = await ReadAllAsync(
            transport.ClaimStaleAsync(
                "events.ack",
                "consumer-recovery",
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            ),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(claimed);
    }

    [Fact]
    public async Task ClaimStaleAsync_TransfersPendingMessageAndIncrementsDeliveryCount()
    {
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        await transport.PublishAsync(
            "events.retry",
            CreateMessage("retry-message"),
            TestContext.Current.CancellationToken
        );
        var original = await ReadOneAsync(transport, "events.retry", "consumer-original");

        var firstClaim = Assert.Single(
            await ReadAllAsync(
                transport.ClaimStaleAsync(
                    "events.retry",
                    "consumer-next",
                    TimeSpan.Zero,
                    TestContext.Current.CancellationToken
                ),
                TestContext.Current.CancellationToken
            )
        );
        var secondClaim = Assert.Single(
            await ReadAllAsync(
                transport.ClaimStaleAsync(
                    "events.retry",
                    "consumer-final",
                    TimeSpan.Zero,
                    TestContext.Current.CancellationToken
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(1, original.DeliveryCount);
        Assert.Equal(original.MessageId, firstClaim.MessageId);
        Assert.Equal(2, firstClaim.DeliveryCount);
        Assert.Equal(original.MessageId, secondClaim.MessageId);
        Assert.Equal(3, secondClaim.DeliveryCount);
    }

    [Fact]
    public async Task ClaimStaleAsync_MoreThan100PendingMessages_ClaimsEveryPage()
    {
        const int messageCount = 205;
        const string topic = "events.paged";
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        for (var index = 0; index < messageCount; index++)
        {
            await transport.PublishAsync(
                topic,
                CreateMessage($"message-{index}"),
                TestContext.Current.CancellationToken
            );
        }

        var pending = await ReadManyAsync(transport, topic, "consumer-original", messageCount);
        var claimed = await ReadAllAsync(
            transport.ClaimStaleAsync(topic, "consumer-recovery", TimeSpan.Zero, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(messageCount, claimed.Count);
        Assert.Equal(
            pending.Select(delivery => delivery.MessageId).OrderBy(messageId => messageId, StringComparer.Ordinal),
            claimed.Select(delivery => delivery.MessageId).OrderBy(messageId => messageId, StringComparer.Ordinal)
        );
        Assert.All(claimed, delivery => Assert.Equal(2, delivery.DeliveryCount));
    }

    [Fact]
    public async Task ClaimStaleAsync_ConsumerStopsAfterFirstPrefetchedMessage_RecoversEntireReadBatch()
    {
        const int messageCount = 5;
        const string topic = "events.prefetched";
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        var messageIds = new List<string>(messageCount);
        for (var index = 0; index < messageCount; index++)
        {
            messageIds.Add(
                await transport.PublishAsync(
                    topic,
                    CreateMessage($"message-{index}"),
                    TestContext.Current.CancellationToken
                )
            );
        }

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (
            var enumerator = transport
                .ConsumeAsync(topic, "consumer-original", cancellationTokenSource.Token)
                .GetAsyncEnumerator(cancellationTokenSource.Token)
        )
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(messageIds[0], enumerator.Current.MessageId);
        }

        var claimed = await ReadAllAsync(
            transport.ClaimStaleAsync(topic, "consumer-recovery", TimeSpan.Zero, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            messageIds.OrderBy(messageId => messageId, StringComparer.Ordinal),
            claimed.Select(delivery => delivery.MessageId).OrderBy(messageId => messageId, StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task PublishAsync_EmptyTopic_ThrowsInvalidParam()
    {
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            transport
                .PublishAsync(string.Empty, CreateMessage("message"), TestContext.Current.CancellationToken)
                .AsTask()
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task AckAsync_MissingTopicOrMessageId_ThrowsInvalidParam()
    {
        var transport = CreateTransport(Guid.NewGuid().ToString("N"));
        var invalidDeliveries = new[]
        {
            new TransportDelivery
            {
                Topic = string.Empty,
                Message = CreateMessage("message"),
                MessageId = "1-0",
                DeliveryCount = 1,
            },
            new TransportDelivery
            {
                Topic = "events.ack",
                Message = CreateMessage("message"),
                MessageId = string.Empty,
                DeliveryCount = 1,
            },
        };

        foreach (var delivery in invalidDeliveries)
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                transport.AckAsync(delivery, TestContext.Current.CancellationToken).AsTask()
            );
            Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        }
    }

    [Fact]
    public void RedisEventTransport_ColonInServerId_ThrowsInvalidParam()
    {
        var exception = Assert.Throws<AgwException>(() => CreateTransport("server:partition"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private RedisEventTransport CreateTransport(string serverId) =>
        new(
            _redis.Connection,
            new Agw.Infrastructure.Events.CloudEventEnvelopeSerializer(),
            serverId,
            NullLogger<RedisEventTransport>.Instance,
            streamMaxLength: 100_000
        );

    private static CloudEventEnvelope CreateMessage(string id, CloudEventDataKind dataKind = CloudEventDataKind.Json)
    {
        CloudEventData data = dataKind switch
        {
            CloudEventDataKind.Json => new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = "{\"name\":\"item\",\"count\":3}",
                Json = ParseJsonData(),
            },
            CloudEventDataKind.Text => new CloudEventData { Kind = CloudEventDataKind.Text, Text = "item text" },
            CloudEventDataKind.Binary => new CloudEventData
            {
                Kind = CloudEventDataKind.Binary,
                Binary = [0, 1, 127, 255],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(dataKind), dataKind, null),
        };

        return new CloudEventEnvelope
        {
            Id = id,
            Source = "/agw/tests",
            Type = "com.agw.tests.event",
            Time = DateTimeOffset.Parse("2026-11-08T00:00:00Z"),
            Subject = "items/3",
            DataContentType = dataKind switch
            {
                CloudEventDataKind.Json => "application/json",
                CloudEventDataKind.Text => "text/plain",
                CloudEventDataKind.Binary => "application/octet-stream",
                _ => throw new ArgumentOutOfRangeException(nameof(dataKind), dataKind, null),
            },
            DataSchema = "https://example.com/schemas/item",
            Data = data,
            Extensions = new Dictionary<string, CloudEventAttributeValue>
            {
                ["scope"] = new(CloudEventAttributeKind.String, "workspace"),
                ["tags"] = new(CloudEventAttributeKind.String, "state:open,priority:high"),
                ["agweventid"] = new(CloudEventAttributeKind.String, "event-42"),
                ["agwdeliveryid"] = new(CloudEventAttributeKind.String, "delivery-42"),
            },
        };
    }

    private static void AssertCloudEventEqual(CloudEventEnvelope expected, CloudEventEnvelope actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.DataContentType, actual.DataContentType);
        Assert.Equal(expected.DataSchema, actual.DataSchema);
        Assert.NotNull(expected.Data);
        Assert.NotNull(actual.Data);
        Assert.Equal(expected.Data.Kind, actual.Data.Kind);
        switch (expected.Data.Kind)
        {
            case CloudEventDataKind.Json:
                Assert.Equal(expected.Data.Json!.Value.GetRawText(), actual.Data.Json!.Value.GetRawText());
                break;
            case CloudEventDataKind.Text:
                Assert.Equal(expected.Data.Text, actual.Data.Text);
                break;
            case CloudEventDataKind.Binary:
                Assert.Equal(expected.Data.Binary, actual.Data.Binary);
                break;
        }
        Assert.Equal(expected.Extensions.Count, actual.Extensions.Count);
        foreach (var (name, value) in expected.Extensions)
        {
            Assert.Equal(value, actual.Extensions[name]);
        }
    }

    private static JsonElement ParseJsonData()
    {
        using var document = JsonDocument.Parse("{\"name\":\"item\",\"count\":3}");
        return document.RootElement.Clone();
    }

    private static async Task<TransportDelivery> ReadOneAsync(IEventTransport transport, string topic, string consumer)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = transport
            .ConsumeAsync(topic, consumer, cancellationTokenSource.Token)
            .GetAsyncEnumerator(cancellationTokenSource.Token);

        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static async Task<IReadOnlyList<TransportDelivery>> ReadManyAsync(
        IEventTransport transport,
        string topic,
        string consumer,
        int count
    )
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var enumerator = transport
            .ConsumeAsync(topic, consumer, cancellationTokenSource.Token)
            .GetAsyncEnumerator(cancellationTokenSource.Token);
        var deliveries = new List<TransportDelivery>(count);
        while (deliveries.Count < count)
        {
            Assert.True(await enumerator.MoveNextAsync());
            deliveries.Add(enumerator.Current);
        }

        return deliveries;
    }

    private static async Task<IReadOnlyList<TransportDelivery>> ReadAllAsync(
        IAsyncEnumerable<TransportDelivery> source,
        CancellationToken cancellationToken
    )
    {
        var deliveries = new List<TransportDelivery>();
        await foreach (var delivery in source.WithCancellation(cancellationToken))
        {
            deliveries.Add(delivery);
        }

        return deliveries;
    }
}

public sealed class RedisServerFixture : IAsyncLifetime
{
    private readonly string _repositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../")
    );
    private Process? _process;
    private string? _instanceDirectory;
    private int _port;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var executable = IsolatedTestConnections.RedisServerExecutable ?? "redis-server";
        _instanceDirectory = Path.Combine(
            _repositoryRoot,
            ".scratch",
            "event-transport",
            "redis-tests",
            Guid.CreateVersion7().ToString("N")
        );
        Directory.CreateDirectory(_instanceDirectory);
        _port = GetAvailablePort();

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _instanceDirectory,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--bind");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(_port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--protected-mode");
        startInfo.ArgumentList.Add("no");
        startInfo.ArgumentList.Add("--save");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("--appendonly");
        startInfo.ArgumentList.Add("no");
        startInfo.ArgumentList.Add("--daemonize");
        startInfo.ArgumentList.Add("no");
        startInfo.ArgumentList.Add("--dir");
        startInfo.ArgumentList.Add(_instanceDirectory);
        startInfo.ArgumentList.Add("--logfile");
        startInfo.ArgumentList.Add(Path.Combine(_instanceDirectory, "redis.log"));

        try
        {
            _process =
                Process.Start(startInfo) ?? throw new InvalidOperationException("Redis process could not be started.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Could not start Redis executable '{executable}'. Install Redis so `redis-server` is on PATH or set `ServerExecutable` in .scratch/redis-tests.json.",
                exception
            );
        }

        var options = ConfigurationOptions.Parse($"127.0.0.1:{_port}");
        options.AbortOnConnectFail = false;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);

        var timeout = Stopwatch.StartNew();
        while (!Connection.IsConnected && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Redis exited with code {_process.ExitCode}. See '{Path.Combine(_instanceDirectory, "redis.log")}'."
                );
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        if (!Connection.IsConnected)
        {
            throw new TimeoutException(
                $"Redis did not accept a connection on port {_port}. See '{Path.Combine(_instanceDirectory, "redis.log")}'."
            );
        }

        await Connection.GetDatabase().PingAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection != null)
        {
            await Connection.DisposeAsync();
        }

        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }

        if (_instanceDirectory is not null && Directory.Exists(_instanceDirectory))
        {
            Directory.Delete(_instanceDirectory, recursive: true);
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
