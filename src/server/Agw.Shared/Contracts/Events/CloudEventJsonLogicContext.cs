using System.Text.Json.Nodes;
using Agw.Shared.Exceptions;

namespace Agw.Shared.Contracts.Events;

public static class CloudEventJsonLogicContext
{
    public static JsonObject Create(CloudEventEnvelope cloudEvent) =>
        Create(
            cloudEvent.Id,
            cloudEvent.Source,
            cloudEvent.Type,
            cloudEvent.Time,
            cloudEvent.Subject,
            cloudEvent.DataContentType,
            cloudEvent.DataSchema,
            ReadStringExtension(cloudEvent, "scope"),
            ReadTags(cloudEvent),
            cloudEvent.Data
        );

    public static JsonObject Create(CloudEventDraft cloudEvent) =>
        Create(
            cloudEvent.Id,
            cloudEvent.Source,
            cloudEvent.Type,
            cloudEvent.Time,
            cloudEvent.Subject,
            cloudEvent.DataContentType,
            cloudEvent.DataSchema,
            cloudEvent.Scope,
            cloudEvent.Tags ?? [],
            cloudEvent.Data
        );

    private static JsonObject Create(
        string id,
        string? source,
        string type,
        DateTimeOffset? time,
        string? subject,
        string? dataContentType,
        string? dataSchema,
        string? scope,
        IReadOnlyList<string> tags,
        CloudEventData? data
    ) =>
        new()
        {
            ["source"] = source,
            ["id"] = id,
            ["type"] = type,
            ["subject"] = subject,
            ["time"] = time is { } value ? JsonValue.Create(value) : null,
            ["datacontenttype"] = dataContentType,
            ["dataschema"] = dataSchema,
            ["scope"] = scope,
            ["tags"] = CreateTags(tags),
            ["data"] = ReadData(data),
        };

    private static string? ReadStringExtension(CloudEventEnvelope cloudEvent, string name) =>
        cloudEvent.Extensions.TryGetValue(name, out var extension) ? extension.Value : null;

    private static IReadOnlyList<string> ReadTags(CloudEventEnvelope cloudEvent) =>
        cloudEvent.Extensions.TryGetValue("tags", out var extension) ? EventTagCodec.Decode(extension.Value) : [];

    private static JsonArray CreateTags(IReadOnlyList<string> tags)
    {
        var result = new JsonArray();
        foreach (var tag in tags)
            result.Add(tag);
        return result;
    }

    private static JsonNode? ReadData(CloudEventData? data) =>
        data is null
            ? null
            : data.Kind switch
            {
                CloudEventDataKind.Json => JsonNode.Parse(data.Json?.GetRawText() ?? data.Text ?? "null"),
                CloudEventDataKind.Text => JsonValue.Create(data.Text),
                CloudEventDataKind.Binary => null,
                _ => throw new AgwException(ErrorCodes.InvalidParam, "The CloudEvents data kind is invalid."),
            };
}
