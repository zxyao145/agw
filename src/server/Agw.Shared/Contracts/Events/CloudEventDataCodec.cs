using System.Net.Mime;
using System.Text.Json;
using Agw.Shared.Exceptions;

namespace Agw.Shared.Contracts.Events;

public static class CloudEventDataCodec
{
    public static string? StoreData(CloudEventData? data) =>
        data is null
            ? null
            : data.Kind switch
            {
                CloudEventDataKind.Json or CloudEventDataKind.Text => data.Text,
                CloudEventDataKind.Binary => Convert.ToBase64String(data.Binary!),
                _ => throw new AgwException(ErrorCodes.InvalidParam),
            };

    public static CloudEventData? ReadData(string? data, string? contentType, bool isBinary)
    {
        if (data is null)
            return null;
        if (isBinary)
            return new CloudEventData { Kind = CloudEventDataKind.Binary, Binary = Convert.FromBase64String(data) };
        if (!IsJsonContentType(contentType))
            return new CloudEventData { Kind = CloudEventDataKind.Text, Text = data };
        using var document = JsonDocument.Parse(data);
        return new CloudEventData
        {
            Kind = CloudEventDataKind.Json,
            Text = data,
            Json = document.RootElement.Clone(),
        };
    }

    public static bool IsJsonContentType(string? contentType)
    {
        if (contentType is null)
            return true;
        return IsJsonMediaType(new ContentType(contentType).MediaType);
    }

    public static bool IsExplicitJsonContentType(string? contentType) =>
        contentType is not null && IsJsonContentType(contentType);

    public static bool IsJsonMediaType(string? mediaType) =>
        mediaType is not null
        && (
            mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
        );
}
