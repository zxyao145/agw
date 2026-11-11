using System.Text;
using System.Text.Json;
using Agw.Infrastructure.Events;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Events.Tests;

public sealed class CloudEventEnvelopeSerializerTests
{
    private const int DefaultMaxPayloadBytes = 262144;

    [Fact]
    public void Serialize_EnvelopeWithJsonDataAndTypedExtensions_RoundTripsAllAttributes()
    {
        using var document = JsonDocument.Parse("""{"answer":42}""");
        var envelope = new CloudEventEnvelope
        {
            Id = "event-1",
            Source = "/agw/sources/1",
            Type = "com.example.updated",
            Time = new DateTimeOffset(2026, 11, 8, 9, 31, 0, TimeSpan.Zero),
            Subject = "item-42",
            DataContentType = "application/json",
            DataSchema = "https://example.com/schema",
            Data = new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = """{"answer":42}""",
                Json = document.RootElement.Clone(),
            },
            Extensions = new Dictionary<string, CloudEventAttributeValue>
            {
                ["scope"] = new(CloudEventAttributeKind.String, "acme/app"),
                ["tags"] = new(CloudEventAttributeKind.String, "state:open"),
                ["flag"] = new(CloudEventAttributeKind.Boolean, "true"),
                ["count"] = new(CloudEventAttributeKind.Integer, "42"),
                ["payload"] = new(CloudEventAttributeKind.Binary, "AQID"),
                ["origin"] = new(CloudEventAttributeKind.Uri, "https://example.com/item/42"),
                ["reference"] = new(CloudEventAttributeKind.UriReference, "../item/42"),
                ["occurredat"] = new(CloudEventAttributeKind.Timestamp, "2026-11-08T09:31:00Z"),
            },
        };
        var serializer = new CloudEventEnvelopeSerializer(DefaultMaxPayloadBytes, ExtensionKinds());

        var bytes = serializer.Serialize(envelope);
        using var wireDocument = JsonDocument.Parse(bytes);
        var roundTripped = serializer.Deserialize(bytes);

        Assert.Equal("1.0", wireDocument.RootElement.GetProperty("specversion").GetString());
        Assert.Equal(envelope.Id, roundTripped.Id);
        Assert.Equal(envelope.Source, roundTripped.Source);
        Assert.Equal(envelope.Type, roundTripped.Type);
        Assert.Equal(envelope.Time, roundTripped.Time);
        Assert.Equal(envelope.Subject, roundTripped.Subject);
        Assert.Equal(envelope.DataContentType, roundTripped.DataContentType);
        Assert.Equal(envelope.DataSchema, roundTripped.DataSchema);
        Assert.NotNull(roundTripped.Data);
        Assert.Equal(CloudEventDataKind.Json, roundTripped.Data.Kind);
        Assert.Equal(envelope.Data.Text, roundTripped.Data.Text);
        Assert.Equal(JsonValueKind.Object, roundTripped.Data.Json!.Value.ValueKind);
        Assert.Equal(envelope.Extensions, roundTripped.Extensions);
    }

    [Theory]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    [InlineData("text/json")]
    public void Serialize_TextData_UsesStringData(string dataContentType)
    {
        var envelope = CreateEnvelope(
            new CloudEventData { Kind = CloudEventDataKind.Text, Text = "hello" },
            dataContentType: dataContentType
        );
        var serializer = new CloudEventEnvelopeSerializer();

        var bytes = serializer.Serialize(envelope);
        using var document = JsonDocument.Parse(bytes);
        var roundTripped = serializer.Deserialize(bytes);

        Assert.Equal("hello", document.RootElement.GetProperty("data").GetString());
        Assert.Equal(CloudEventDataKind.Text, roundTripped.Data!.Kind);
        Assert.Equal("hello", roundTripped.Data.Text);
    }

    [Fact]
    public void Serialize_BinaryData_UsesBase64Data()
    {
        var envelope = CreateEnvelope(new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = [1, 2, 3] });
        var serializer = new CloudEventEnvelopeSerializer();

        var bytes = serializer.Serialize(envelope);
        using var document = JsonDocument.Parse(bytes);
        var roundTripped = serializer.Deserialize(bytes);

        Assert.Equal("AQID", document.RootElement.GetProperty("data_base64").GetString());
        Assert.Equal(new byte[] { 1, 2, 3 }, roundTripped.Data!.Binary);
    }

    [Fact]
    public void Serialize_JsonNullData_PreservesPresentNull()
    {
        using var document = JsonDocument.Parse("null");
        var envelope = CreateEnvelope(
            new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = "null",
                Json = document.RootElement.Clone(),
            },
            dataContentType: "application/json"
        );
        var serializer = new CloudEventEnvelopeSerializer();

        var roundTripped = serializer.Deserialize(serializer.Serialize(envelope));

        Assert.NotNull(roundTripped.Data);
        Assert.Equal(CloudEventDataKind.Json, roundTripped.Data.Kind);
        Assert.Equal("null", roundTripped.Data.Text);
        Assert.Equal(JsonValueKind.Null, roundTripped.Data.Json!.Value.ValueKind);
    }

    [Fact]
    public void Serialize_AbsentData_RemainsAbsent()
    {
        var serializer = new CloudEventEnvelopeSerializer();

        var roundTripped = serializer.Deserialize(serializer.Serialize(CreateEnvelope(data: null)));

        Assert.Null(roundTripped.Data);
    }

    [Fact]
    public void Serialize_JsonTextAndValueDiffer_ThrowsInvalidParam()
    {
        using var document = JsonDocument.Parse("""{"answer":43}""");
        var envelope = CreateEnvelope(
            new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = """{"answer":42}""",
                Json = document.RootElement.Clone(),
            },
            dataContentType: "application/json"
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer().Serialize(envelope));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Serialize_JsonPayloadSizeUsesEncodedNumericFormat_ThrowsPayloadTooLarge()
    {
        using var document = JsonDocument.Parse("1.0");
        var envelope = CreateEnvelope(
            new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = "1",
                Json = document.RootElement.Clone(),
            },
            dataContentType: "application/json"
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer(1).Serialize(envelope));

        Assert.Equal(ErrorCodes.EventPayloadTooLarge.Code, exception.Code);
    }

    [Fact]
    public void Serialize_UnregisteredTypedExtension_ThrowsInvalidParam()
    {
        var envelope = CreateEnvelope(
            data: null,
            extensions: new Dictionary<string, CloudEventAttributeValue>
            {
                ["flag"] = new(CloudEventAttributeKind.Boolean, "true"),
            }
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer().Serialize(envelope));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Serialize_ConflictingPlatformExtensionSchema_ThrowsInvalidParam()
    {
        var extensionKinds = new Dictionary<string, CloudEventAttributeKind>
        {
            ["tags"] = CloudEventAttributeKind.Boolean,
        };

        var exception = Assert.Throws<AgwException>(() =>
            new CloudEventEnvelopeSerializer(DefaultMaxPayloadBytes, extensionKinds)
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Deserialize_SpecVersion03_ThrowsInvalidParam()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"specversion":"0.3","id":"event-1","source":"/agw/sources/1","type":"com.example.updated"}"""
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer().Deserialize(bytes));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Serialize_InvalidTagsExtension_ThrowsInvalidParam()
    {
        var envelope = CreateEnvelope(
            data: null,
            extensions: new Dictionary<string, CloudEventAttributeValue>
            {
                ["tags"] = new(CloudEventAttributeKind.String, "state:open,,owner:agw"),
            }
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer().Serialize(envelope));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Deserialize_InvalidTagsExtension_ThrowsInvalidParam()
    {
        var bytes = Encoding.UTF8.GetBytes(
            """{"specversion":"1.0","id":"event-1","source":"/agw/sources/1","type":"com.example.updated","tags":"state:open,,owner:agw"}"""
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer().Deserialize(bytes));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Serialize_AbsoluteSourceAndSchema_PreserveOriginalStrings()
    {
        var source = "https://example.test";
        var schema = "https://example.test/schema%2Fv1";
        var envelope = new CloudEventEnvelope
        {
            Id = "event-1",
            Source = source,
            Type = "com.example.updated",
            DataSchema = schema,
        };
        var serializer = new CloudEventEnvelopeSerializer();

        var bytes = serializer.Serialize(envelope);
        using var document = JsonDocument.Parse(bytes);
        var roundTripped = serializer.Deserialize(bytes);

        Assert.Equal(source, document.RootElement.GetProperty("source").GetString());
        Assert.Equal(source, roundTripped.Source);
        Assert.Equal(schema, document.RootElement.GetProperty("dataschema").GetString());
        Assert.Equal(schema, roundTripped.DataSchema);
    }

    [Fact]
    public void Serialize_PayloadOverLimit_ThrowsPayloadTooLarge()
    {
        var envelope = CreateEnvelope(
            new CloudEventData { Kind = CloudEventDataKind.Text, Text = "abc" },
            "text/plain"
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer(2).Serialize(envelope));

        Assert.Equal(ErrorCodes.EventPayloadTooLarge.Code, exception.Code);
    }

    [Fact]
    public void Deserialize_PayloadOverLimit_ThrowsPayloadTooLarge()
    {
        var bytes = new CloudEventEnvelopeSerializer().Serialize(
            CreateEnvelope(new CloudEventData { Kind = CloudEventDataKind.Text, Text = "abc" }, "text/plain")
        );

        var exception = Assert.Throws<AgwException>(() => new CloudEventEnvelopeSerializer(2).Deserialize(bytes));

        Assert.Equal(ErrorCodes.EventPayloadTooLarge.Code, exception.Code);
    }

    [Fact]
    public void EncodeAndDecode_Tags_JoinAndSplitCommaSeparatedValues()
    {
        var tags = new[] { "state:open", "owner:agw" };

        var encoded = EventTagCodec.Encode(tags);
        var decoded = EventTagCodec.Decode(encoded);

        Assert.Equal("state:open,owner:agw", encoded);
        Assert.Equal(tags, decoded);
        Assert.Empty(EventTagCodec.Decode(string.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData("state,open")]
    [InlineData("line\nbreak")]
    public void Encode_InvalidTag_ThrowsInvalidParam(string tag)
    {
        var exception = Assert.Throws<AgwException>(() => EventTagCodec.Encode([tag]));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    [Fact]
    public void Deserialize_EmptyTag_ThrowsInvalidParam()
    {
        var exception = Assert.Throws<AgwException>(() => EventTagCodec.Decode("state:open,,owner:agw"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
    }

    private static CloudEventEnvelope CreateEnvelope(
        CloudEventData? data = null,
        string? dataContentType = null,
        IReadOnlyDictionary<string, CloudEventAttributeValue>? extensions = null
    ) =>
        new()
        {
            Id = "event-1",
            Source = "/agw/sources/1",
            Type = "com.example.updated",
            DataContentType = dataContentType,
            Data = data,
            Extensions = extensions ?? new Dictionary<string, CloudEventAttributeValue>(),
        };

    private static IReadOnlyDictionary<string, CloudEventAttributeKind> ExtensionKinds() =>
        new Dictionary<string, CloudEventAttributeKind>
        {
            ["scope"] = CloudEventAttributeKind.String,
            ["tags"] = CloudEventAttributeKind.String,
            ["flag"] = CloudEventAttributeKind.Boolean,
            ["count"] = CloudEventAttributeKind.Integer,
            ["payload"] = CloudEventAttributeKind.Binary,
            ["origin"] = CloudEventAttributeKind.Uri,
            ["reference"] = CloudEventAttributeKind.UriReference,
            ["occurredat"] = CloudEventAttributeKind.Timestamp,
        };
}
