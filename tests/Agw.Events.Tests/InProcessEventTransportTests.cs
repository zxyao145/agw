using System.Text.Json;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Infrastructure.Events.Transport;

public sealed class InProcessEventTransportTests
{
    private const string JsonPayloadText = "{\"value\":42}";

    private readonly CloudEventEnvelopeSerializer _serializer = new();
    private readonly InProcessEventTransport _transport;

    public InProcessEventTransportTests()
    {
        _transport = new InProcessEventTransport(_serializer);
    }

    public static TheoryData<CloudEventData> PayloadModes =>
        new()
        {
            CreateJsonData(),
            new CloudEventData { Kind = CloudEventDataKind.Text, Text = "hello" },
            new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = [1, 2, 3] },
        };

    [Theory]
    [MemberData(nameof(PayloadModes))]
    public async Task PublishAsync_CloudEventPayloadMode_RoundTrips(CloudEventData data)
    {
        var message = CreateEnvelope(data);

        string messageId = await _transport.PublishAsync(
            "agw.events.accepted",
            message,
            TestContext.Current.CancellationToken
        );
        TransportDelivery delivery = await ReadOneAsync("agw.events.accepted", "consumer-a");

        Assert.Equal(messageId, delivery.MessageId);
        Assert.Equal("agw.events.accepted", delivery.Topic);
        Assert.Equal(1, delivery.DeliveryCount);
        AssertEnvelopeEqual(message, delivery.Message);
    }

    [Fact]
    public async Task ConsumeAsync_DifferentTopics_ReceivesOnlyPublishedTopic()
    {
        CloudEventEnvelope message = CreateEnvelope(CreateJsonData());
        string messageId = await _transport.PublishAsync("topic-a", message, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var otherTopic = _transport
            .ConsumeAsync("topic-b", "consumer-b", cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Task<bool> pendingRead = otherTopic.MoveNextAsync().AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead);
        TransportDelivery delivery = await ReadOneAsync("topic-a", "consumer-a");

        Assert.Equal(messageId, delivery.MessageId);
        AssertEnvelopeEqual(message, delivery.Message);
    }

    [Fact]
    public async Task ConsumeAsync_CompetingConsumers_DistributesEachPublishedMessageOnce()
    {
        CloudEventEnvelope message = CreateEnvelope(CreateJsonData());
        string firstId = await _transport.PublishAsync("topic", message, TestContext.Current.CancellationToken);
        string secondId = await _transport.PublishAsync("topic", message, TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            timeout.Token
        );
        await using var firstConsumer = _transport
            .ConsumeAsync("topic", "consumer-a", cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        await using var secondConsumer = _transport
            .ConsumeAsync("topic", "consumer-b", cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Task<bool> firstRead = firstConsumer.MoveNextAsync().AsTask();
        Task<bool> secondRead = secondConsumer.MoveNextAsync().AsTask();
        bool[] received = await Task.WhenAll(firstRead, secondRead).WaitAsync(cancellation.Token);

        Assert.All(received, result => Assert.True(result));
        Assert.Equal(
            new[] { firstId, secondId }.Order(),
            new[] { firstConsumer.Current.MessageId, secondConsumer.Current.MessageId }.Order()
        );
    }

    [Fact]
    public async Task PublishAsync_MutableBinaryData_UsesPublishedSnapshot()
    {
        byte[] binary = [1, 2, 3];
        CloudEventEnvelope message = CreateEnvelope(
            new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = binary }
        );
        await _transport.PublishAsync("topic", message, TestContext.Current.CancellationToken);
        binary[0] = 9;

        TransportDelivery delivery = await ReadOneAsync("topic", "consumer");

        Assert.Equal(new byte[] { 1, 2, 3 }, delivery.Message.Data!.Binary);
    }

    [Fact]
    public async Task PublishAsync_DuplicateCloudEvent_CreatesDistinctTransportIds()
    {
        CloudEventEnvelope message = CreateEnvelope(CreateJsonData());

        string firstId = await _transport.PublishAsync("topic", message, TestContext.Current.CancellationToken);
        string secondId = await _transport.PublishAsync("topic", message, TestContext.Current.CancellationToken);
        TransportDelivery first = await ReadOneAsync("topic", "consumer");
        TransportDelivery second = await ReadOneAsync("topic", "consumer");

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(firstId, first.MessageId);
        Assert.Equal(secondId, second.MessageId);
    }

    [Fact]
    public async Task ConsumeAsync_Cancellation_StopsWaitingRead()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var consumer = _transport
            .ConsumeAsync("topic", "consumer", cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Task<bool> pendingRead = consumer.MoveNextAsync().AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead);
    }

    [Fact]
    public async Task AckAsync_Delivery_CompletesWithoutPersistence()
    {
        await _transport.PublishAsync("topic", CreateEnvelope(CreateJsonData()), TestContext.Current.CancellationToken);
        TransportDelivery delivery = await ReadOneAsync("topic", "consumer");

        await _transport.AckAsync(delivery, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClaimStaleAsync_ValidRequest_ReturnsNoDeliveries()
    {
        await _transport.PublishAsync("topic", CreateEnvelope(CreateJsonData()), TestContext.Current.CancellationToken);

        List<TransportDelivery> deliveries = [];
        await foreach (
            TransportDelivery delivery in _transport.ClaimStaleAsync(
                "topic",
                "consumer",
                TimeSpan.FromMinutes(1),
                TestContext.Current.CancellationToken
            )
        )
            deliveries.Add(delivery);

        Assert.Empty(deliveries);
    }

    [Fact]
    public async Task PublishAsync_EmptyTopic_ThrowsInvalidParam()
    {
        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await _transport.PublishAsync(" ", CreateEnvelope(CreateJsonData()), TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public async Task ConsumeAsync_InvalidTopicOrConsumer_ThrowsInvalidParam()
    {
        AgwException topicException = await Assert.ThrowsAsync<AgwException>(() => ReadOneAsync(" ", "consumer"));
        AgwException consumerException = await Assert.ThrowsAsync<AgwException>(() => ReadOneAsync("topic", " "));

        Assert.Equal(ErrorCodes.InvalidParam.Code, topicException.Code);
        Assert.Equal(ErrorCodes.InvalidParam.Code, consumerException.Code);
    }

    [Fact]
    public async Task ClaimStaleAsync_InvalidTopicConsumerOrMinIdle_ThrowsInvalidParam()
    {
        AgwException topicException = await Assert.ThrowsAsync<AgwException>(() =>
            DrainClaimsAsync(" ", "consumer", TimeSpan.Zero)
        );
        AgwException consumerException = await Assert.ThrowsAsync<AgwException>(() =>
            DrainClaimsAsync("topic", " ", TimeSpan.Zero)
        );
        AgwException minIdleException = await Assert.ThrowsAsync<AgwException>(() =>
            DrainClaimsAsync("topic", "consumer", TimeSpan.FromTicks(-1))
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, topicException.Code);
        Assert.Equal(ErrorCodes.InvalidParam.Code, consumerException.Code);
        Assert.Equal(ErrorCodes.InvalidParam.Code, minIdleException.Code);
    }

    private async Task<TransportDelivery> ReadOneAsync(string topic, string consumer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken,
            timeout.Token
        );
        await using var enumerator = _transport.ConsumeAsync(topic, consumer, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private async Task DrainClaimsAsync(string topic, string consumer, TimeSpan minIdle)
    {
        await foreach (
            TransportDelivery _ in _transport.ClaimStaleAsync(
                topic,
                consumer,
                minIdle,
                TestContext.Current.CancellationToken
            )
        ) { }
    }

    private static CloudEventEnvelope CreateEnvelope(CloudEventData data) =>
        new()
        {
            Id = "event-id",
            Source = "/agw/sources/source-id",
            Type = "example.event",
            Time = new DateTimeOffset(2026, 11, 8, 9, 31, 0, TimeSpan.Zero),
            Subject = "subject",
            DataContentType = data.Kind switch
            {
                CloudEventDataKind.Json => "application/json",
                CloudEventDataKind.Text => "text/plain",
                CloudEventDataKind.Binary => "application/octet-stream",
                _ => throw new ArgumentOutOfRangeException(nameof(data)),
            },
            DataSchema = "https://example.test/schema",
            Data = data,
            Extensions = new Dictionary<string, CloudEventAttributeValue>
            {
                ["scope"] = new(CloudEventAttributeKind.String, "workspace"),
            },
        };

    private static JsonElement ParseJson(string value)
    {
        using JsonDocument document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static CloudEventData CreateJsonData() =>
        new()
        {
            Kind = CloudEventDataKind.Json,
            Text = JsonPayloadText,
            Json = ParseJson(JsonPayloadText),
        };

    private static void AssertEnvelopeEqual(CloudEventEnvelope expected, CloudEventEnvelope actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Source, actual.Source);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Time, actual.Time);
        Assert.Equal(expected.Subject, actual.Subject);
        Assert.Equal(expected.DataContentType, actual.DataContentType);
        Assert.Equal(expected.DataSchema, actual.DataSchema);
        Assert.Equal(expected.Extensions, actual.Extensions);
        Assert.Equal(expected.Data!.Kind, actual.Data!.Kind);

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
            default:
                throw new ArgumentOutOfRangeException(nameof(expected));
        }
    }
}
