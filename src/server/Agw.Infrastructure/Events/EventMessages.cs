using System.Text.Json;
using Agw.Events.Contracts.Transport;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Data.Entities.Events;
using Agw.Shared.Exceptions;

namespace Agw.Infrastructure.Events;

public static class EventMessages
{
    public const string Accepted = EventTopics.Accepted;
    public const string Delivery = EventTopics.Delivery;
    public const string NotificationDispatch = EventTopics.NotificationDispatch;

    public static CloudEventEnvelope FromRecord(EventRecord record, string? locator = null, Guid? locatorId = null)
    {
        var extensions = new Dictionary<string, CloudEventAttributeValue>(StringComparer.Ordinal);
        if (record.Scope is not null)
            extensions.Add("scope", new(CloudEventAttributeKind.String, record.Scope));
        if (record.TagsJson is not null)
        {
            var tags =
                JsonSerializer.Deserialize<string[]>(record.TagsJson)
                ?? throw new AgwException(ErrorCodes.InvalidParam);
            extensions.Add("tags", new(CloudEventAttributeKind.String, EventTagCodec.Encode(tags)));
        }
        if (locator is not null)
            extensions.Add(locator, new(CloudEventAttributeKind.String, (locatorId ?? record.Id).ToString()));
        return new CloudEventEnvelope
        {
            Id = record.CeId,
            Source = record.CeSource,
            Type = record.Type,
            Time = record.CeTime,
            Subject = record.Subject,
            DataContentType = record.CeDataContentType,
            DataSchema = record.CeDataSchema,
            Data = ReadData(record),
            Extensions = extensions,
        };
    }

    public static CloudEventData? ReadData(EventRecord record) =>
        CloudEventDataCodec.ReadData(record.CeData, record.CeDataContentType, record.CeDataIsBinary);
}
