using System.Text.Json;
using Agw.Events.Application;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Api.Dtos;

public static class CloudEventResponseMapper
{
    public static CloudEventResponse From(EventCloudEvent cloudEvent) =>
        new()
        {
            SpecVersion = "1.0",
            Id = cloudEvent.Id,
            Source = cloudEvent.Source,
            Type = cloudEvent.Type,
            Time = cloudEvent.Time,
            Subject = cloudEvent.Subject,
            DataContentType = cloudEvent.DataContentType,
            DataSchema = cloudEvent.DataSchema,
            Data = ReadData(cloudEvent.Data),
            DataBase64 = cloudEvent.Data is { Kind: CloudEventDataKind.Binary, Binary: { } binary }
                ? Convert.ToBase64String(binary)
                : null,
            Scope = cloudEvent.Scope,
            Tags = cloudEvent.Tags,
        };

    private static JsonElement? ReadData(CloudEventData? data) =>
        data is null
            ? null
            : data.Kind switch
            {
                CloudEventDataKind.Json => data.Json ?? JsonSerializer.Deserialize<JsonElement>(data.Text ?? "null"),
                CloudEventDataKind.Text => JsonSerializer.SerializeToElement(data.Text),
                CloudEventDataKind.Binary => null,
                _ => throw new AgwException(ErrorCodes.InvalidParam, "The CloudEvents data kind is invalid."),
            };
}
