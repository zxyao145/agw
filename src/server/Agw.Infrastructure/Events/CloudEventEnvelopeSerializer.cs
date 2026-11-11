using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using CloudNative.CloudEvents;
using CloudNative.CloudEvents.AspNetCore;
using CloudNative.CloudEvents.SystemTextJson;
using Microsoft.AspNetCore.Http;

namespace Agw.Infrastructure.Events;

public sealed class CloudEventEnvelopeSerializer : ICloudEventEnvelopeSerializer
{
    private const int DefaultMaxPayloadBytes = 262144;
    private const string StructuredContentType = "application/cloudevents+json";
    private const string DataPropertyName = "data";
    private const string DataBase64PropertyName = "data_base64";

    private static readonly HashSet<string> CoreAttributeNames = new(StringComparer.Ordinal)
    {
        "specversion",
        "id",
        "source",
        "type",
        "time",
        "subject",
        "datacontenttype",
        "dataschema",
    };

    private static readonly IReadOnlyDictionary<string, CloudEventAttributeKind> PlatformExtensionKinds =
        new Dictionary<string, CloudEventAttributeKind>(StringComparer.Ordinal)
        {
            ["scope"] = CloudEventAttributeKind.String,
            ["tags"] = CloudEventAttributeKind.String,
            ["agweventid"] = CloudEventAttributeKind.String,
            ["agwdeliveryid"] = CloudEventAttributeKind.String,
        };

    private readonly int _maxPayloadBytes;
    private readonly IReadOnlyDictionary<string, CloudEventAttribute> _extensionAttributes;
    private readonly IReadOnlyList<CloudEventAttribute> _extensionAttributeList;
    private readonly JsonEventFormatter _formatter = new EnvelopeJsonEventFormatter();

    public CloudEventEnvelopeSerializer(int maxPayloadBytes = DefaultMaxPayloadBytes)
        : this(maxPayloadBytes, new Dictionary<string, CloudEventAttributeKind>()) { }

    public CloudEventEnvelopeSerializer(
        int maxPayloadBytes,
        IReadOnlyDictionary<string, CloudEventAttributeKind> extensionKinds
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayloadBytes);
        ArgumentNullException.ThrowIfNull(extensionKinds);
        _maxPayloadBytes = maxPayloadBytes;

        var kinds = PlatformExtensionKinds.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal
        );
        foreach (var (name, kind) in extensionKinds)
        {
            ValidateExtensionName(name);
            if (PlatformExtensionKinds.TryGetValue(name, out var platformKind))
            {
                if (kind != platformKind)
                    throw InvalidParam();
                continue;
            }

            if (IsReservedName(name) || !Enum.IsDefined(kind))
                throw InvalidParam();
            kinds[name] = kind;
        }

        _extensionAttributes = kinds.ToDictionary(
            static pair => pair.Key,
            static pair => CloudEventAttribute.CreateExtension(pair.Key, GetSdkAttributeType(pair.Value)),
            StringComparer.Ordinal
        );
        _extensionAttributeList = _extensionAttributes.Values.ToArray();
    }

    public byte[] Serialize(CloudEventEnvelope message)
    {
        if (message is null)
            throw InvalidParam();

        try
        {
            var cloudEvent = ToCloudEvent(message);
            if (!cloudEvent.IsValid)
                throw InvalidParam();

            var bytes = _formatter.EncodeStructuredModeMessage(cloudEvent, out _).ToArray();
            EnsureSerializedPayloadWithinLimit(message.Data, bytes);
            return bytes;
        }
        catch (Exception exception) when (IsInvalidCloudEventException(exception))
        {
            throw InvalidParam(exception);
        }
    }

    public void Validate(CloudEventEnvelope message) => _ = Serialize(message);

    public CloudEventEnvelope Deserialize(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            ValidateWireProperties(document.RootElement);
            var cloudEvent = _formatter.DecodeStructuredModeMessage(
                bytes,
                new ContentType(StructuredContentType),
                _extensionAttributeList
            );
            if (!cloudEvent.IsValid)
                throw InvalidParam();
            if (cloudEvent.SpecVersion.VersionId != "1.0")
                throw InvalidParam();

            var data = ReadData(document.RootElement, cloudEvent);
            EnsurePayloadWithinLimit(data);
            return new CloudEventEnvelope
            {
                Id = cloudEvent.Id!,
                Source = cloudEvent.Source!.OriginalString,
                Type = cloudEvent.Type!,
                Time = cloudEvent.Time,
                Subject = cloudEvent.Subject,
                DataContentType = cloudEvent.DataContentType,
                DataSchema = cloudEvent.DataSchema?.OriginalString,
                Data = data,
                Extensions = ReadExtensions(cloudEvent),
            };
        }
        catch (Exception exception) when (IsInvalidCloudEventException(exception))
        {
            throw InvalidParam(exception);
        }
    }

    public async Task<CloudEventEnvelope> DeserializeHttpAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cloudEvent = await request.ToCloudEventAsync(_formatter, _extensionAttributeList);
            if (
                cloudEvent
                    .GetPopulatedAttributes()
                    .Any(attribute => attribute.Key.Name.StartsWith("agw", StringComparison.Ordinal))
            )
                throw InvalidParam();
            if (!cloudEvent.IsValid || cloudEvent.SpecVersion.VersionId != "1.0")
                throw InvalidParam();
            return Deserialize(_formatter.EncodeStructuredModeMessage(cloudEvent, out _));
        }
        catch (Exception exception) when (IsInvalidCloudEventException(exception))
        {
            throw InvalidParam(exception);
        }
    }

    private CloudEvent ToCloudEvent(CloudEventEnvelope message)
    {
        var cloudEvent = new CloudEvent(_extensionAttributeList)
        {
            Id = message.Id,
            Type = message.Type,
            Subject = message.Subject,
            Time = message.Time,
        };
        cloudEvent.SetAttributeFromString("source", message.Source);
        if (message.DataContentType is not null)
        {
            _ = new ContentType(message.DataContentType);
            cloudEvent.DataContentType = message.DataContentType;
        }
        if (message.DataSchema is not null)
            cloudEvent.SetAttributeFromString("dataschema", message.DataSchema);

        if (message.Extensions is null)
            throw InvalidParam();
        foreach (var (name, extension) in message.Extensions)
        {
            ValidateExtensionName(name);
            if (IsReservedName(name) || extension is null)
                throw InvalidParam();

            var attribute = GetExtensionAttribute(name, extension.Kind);
            if (name == "tags")
                _ = EventTagCodec.Decode(extension.Value);
            var parsed = attribute.Parse(extension.Value);
            cloudEvent[attribute] = parsed;
        }

        if (message.Data is not null)
        {
            ValidateData(message.Data, message.DataContentType);
            cloudEvent.Data = message.Data.Kind switch
            {
                CloudEventDataKind.Json => message.Data.Json!.Value,
                CloudEventDataKind.Text => message.Data.Text,
                CloudEventDataKind.Binary => message.Data.Binary,
                _ => throw InvalidParam(),
            };
        }

        return cloudEvent;
    }

    private CloudEventAttribute GetExtensionAttribute(string name, CloudEventAttributeKind kind)
    {
        if (_extensionAttributes.TryGetValue(name, out var attribute))
        {
            if (GetKind(attribute.Type) != kind)
                throw InvalidParam();
            return attribute;
        }

        if (kind != CloudEventAttributeKind.String)
            throw InvalidParam();
        return CloudEventAttribute.CreateExtension(name, CloudEventAttributeType.String);
    }

    private IReadOnlyDictionary<string, CloudEventAttributeValue> ReadExtensions(CloudEvent cloudEvent)
    {
        var extensions = new Dictionary<string, CloudEventAttributeValue>(StringComparer.Ordinal);
        foreach (var attribute in cloudEvent.ExtensionAttributes)
        {
            if (cloudEvent[attribute] is { } value)
            {
                var formatted = attribute.Format(value);
                if (attribute.Name == "tags")
                    _ = EventTagCodec.Decode(formatted);
                extensions.Add(attribute.Name, new CloudEventAttributeValue(GetKind(attribute.Type), formatted));
            }
        }

        return extensions;
    }

    private CloudEventData? ReadData(JsonElement root, CloudEvent cloudEvent)
    {
        var hasData = root.TryGetProperty(DataPropertyName, out var dataElement);
        var hasBinary = root.TryGetProperty(DataBase64PropertyName, out _);
        if (hasBinary)
        {
            return new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = (byte[])cloudEvent.Data! };
        }

        if (!hasData)
            return null;
        if (dataElement.ValueKind == JsonValueKind.Null)
        {
            var jsonNull = dataElement.Clone();
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = "null",
                Json = jsonNull,
            };
        }

        if (CloudEventDataCodec.IsJsonContentType(cloudEvent.DataContentType))
        {
            if (cloudEvent.Data is not JsonElement json)
                throw InvalidParam();
            var jsonCopy = json.Clone();
            return new CloudEventData
            {
                Kind = CloudEventDataKind.Json,
                Text = jsonCopy.GetRawText(),
                Json = jsonCopy,
            };
        }

        if (cloudEvent.Data is not string text)
            throw InvalidParam();
        return new CloudEventData { Kind = CloudEventDataKind.Text, Text = text };
    }

    private void ValidateWireProperties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw InvalidParam();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasData = false;
        var hasBinary = false;
        JsonElement dataElement = default;
        string? dataContentType = null;
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw InvalidParam();
            if (property.Name == DataPropertyName)
            {
                hasData = true;
                dataElement = property.Value;
                continue;
            }
            if (property.Name == DataBase64PropertyName)
            {
                hasBinary = true;
                if (property.Value.ValueKind == JsonValueKind.Null)
                    throw InvalidParam();
                continue;
            }
            if (property.Value.ValueKind == JsonValueKind.Null)
                throw InvalidParam();
            if (property.Name == "datacontenttype")
                dataContentType = property.Value.GetString();

            if (CoreAttributeNames.Contains(property.Name))
                continue;

            ValidateExtensionName(property.Name);
            if (_extensionAttributes.TryGetValue(property.Name, out var attribute))
            {
                if (!IsExpectedExtensionToken(attribute.Type, property.Value.ValueKind))
                    throw InvalidParam();
            }
            else if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw InvalidParam();
            }
        }

        if (hasData && hasBinary)
            throw InvalidParam();
        if (dataContentType is not null)
            _ = new ContentType(dataContentType);
        if (
            hasData
            && dataElement.ValueKind == JsonValueKind.Null
            && !CloudEventDataCodec.IsJsonContentType(dataContentType)
        )
            throw InvalidParam();
    }

    private void ValidateData(CloudEventData data, string? dataContentType)
    {
        switch (data.Kind)
        {
            case CloudEventDataKind.Json:
                if (data.Json is not { } json || data.Text is null || data.Binary is not null)
                    throw InvalidParam();
                using (var textDocument = JsonDocument.Parse(data.Text))
                {
                    if (!JsonElement.DeepEquals(textDocument.RootElement, json))
                        throw InvalidParam();
                }
                if (dataContentType is not null && !CloudEventDataCodec.IsJsonContentType(dataContentType))
                    throw InvalidParam();
                break;
            case CloudEventDataKind.Text:
                if (data.Text is null || data.Json is not null || data.Binary is not null)
                    throw InvalidParam();
                if (dataContentType is null || CloudEventDataCodec.IsJsonContentType(dataContentType))
                    throw InvalidParam();
                break;
            case CloudEventDataKind.Binary:
                if (data.Binary is null || data.Text is not null || data.Json is not null)
                    throw InvalidParam();
                break;
            default:
                throw InvalidParam();
        }
    }

    private void EnsurePayloadWithinLimit(CloudEventData? data)
    {
        if (data is null)
            return;

        var byteCount = data.Kind switch
        {
            CloudEventDataKind.Json when data.Text is not null => Encoding.UTF8.GetByteCount(data.Text),
            CloudEventDataKind.Text when data.Text is not null => Encoding.UTF8.GetByteCount(data.Text),
            CloudEventDataKind.Binary when data.Binary is not null => data.Binary.Length,
            _ => throw InvalidParam(),
        };
        EnsurePayloadWithinLimit(byteCount);
    }

    private void EnsureSerializedPayloadWithinLimit(CloudEventData? data, ReadOnlyMemory<byte> serializedMessage)
    {
        if (data?.Kind != CloudEventDataKind.Json)
        {
            EnsurePayloadWithinLimit(data);
            return;
        }

        using var document = JsonDocument.Parse(serializedMessage);
        if (!document.RootElement.TryGetProperty(DataPropertyName, out var dataElement))
            throw InvalidParam();
        EnsurePayloadWithinLimit(Encoding.UTF8.GetByteCount(dataElement.GetRawText()));
    }

    private void EnsurePayloadWithinLimit(int byteCount)
    {
        if (byteCount > _maxPayloadBytes)
            throw new AgwException(ErrorCodes.EventPayloadTooLarge);
    }

    private static bool IsExpectedExtensionToken(CloudEventAttributeType type, JsonValueKind valueKind) =>
        type == CloudEventAttributeType.Boolean ? valueKind is JsonValueKind.True or JsonValueKind.False
        : type == CloudEventAttributeType.Integer ? valueKind == JsonValueKind.Number
        : valueKind == JsonValueKind.String;

    private static CloudEventAttributeType GetSdkAttributeType(CloudEventAttributeKind kind) =>
        kind switch
        {
            CloudEventAttributeKind.Boolean => CloudEventAttributeType.Boolean,
            CloudEventAttributeKind.Integer => CloudEventAttributeType.Integer,
            CloudEventAttributeKind.String => CloudEventAttributeType.String,
            CloudEventAttributeKind.Binary => CloudEventAttributeType.Binary,
            CloudEventAttributeKind.Uri => CloudEventAttributeType.Uri,
            CloudEventAttributeKind.UriReference => CloudEventAttributeType.UriReference,
            CloudEventAttributeKind.Timestamp => CloudEventAttributeType.Timestamp,
            _ => throw InvalidParam(),
        };

    private static CloudEventAttributeKind GetKind(CloudEventAttributeType type) =>
        type switch
        {
            var value when value == CloudEventAttributeType.Boolean => CloudEventAttributeKind.Boolean,
            var value when value == CloudEventAttributeType.Integer => CloudEventAttributeKind.Integer,
            var value when value == CloudEventAttributeType.String => CloudEventAttributeKind.String,
            var value when value == CloudEventAttributeType.Binary => CloudEventAttributeKind.Binary,
            var value when value == CloudEventAttributeType.Uri => CloudEventAttributeKind.Uri,
            var value when value == CloudEventAttributeType.UriReference => CloudEventAttributeKind.UriReference,
            var value when value == CloudEventAttributeType.Timestamp => CloudEventAttributeKind.Timestamp,
            _ => throw InvalidParam(),
        };

    private static void ValidateExtensionName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 20 || IsReservedName(name))
            throw InvalidParam();
        if (name.Any(static character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9')))
            throw InvalidParam();
    }

    private static bool IsReservedName(string name) =>
        CoreAttributeNames.Contains(name) || name is DataPropertyName or DataBase64PropertyName;

    private static bool IsInvalidCloudEventException(Exception exception) =>
        exception
            is ArgumentException
                or FormatException
                or InvalidOperationException
                or JsonException
                or OverflowException;

    private static AgwException InvalidParam(Exception? innerException = null) =>
        innerException is null
            ? new AgwException(ErrorCodes.InvalidParam)
            : new AgwException(ErrorCodes.InvalidParam, "CloudEvent envelope is invalid.", innerException);

    private sealed class EnvelopeJsonEventFormatter : JsonEventFormatter
    {
        protected override bool IsJsonMediaType(string mediaType) => CloudEventDataCodec.IsJsonMediaType(mediaType);

        protected override void EncodeStructuredModeData(CloudEvent cloudEvent, Utf8JsonWriter writer)
        {
            if (
                cloudEvent.Data is string text
                && cloudEvent.DataContentType is { } dataContentType
                && !IsJsonMediaType(new ContentType(dataContentType).MediaType)
            )
            {
                writer.WritePropertyName(DataPropertyName);
                writer.WriteStringValue(text);
                return;
            }

            base.EncodeStructuredModeData(cloudEvent, writer);
        }
    }
}
