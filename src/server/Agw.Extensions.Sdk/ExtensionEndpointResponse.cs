using System.Text.Json;

namespace Agw.Extensions.Sdk;

public sealed record ExtensionEndpointResponse
{
    public required int StatusCode { get; init; }

    public required string ContentType { get; init; }

    public JsonElement? JsonBody { get; init; }

    public IAsyncEnumerable<ReadOnlyMemory<byte>>? BodyStream { get; init; }

    public static ExtensionEndpointResponse FromJson(JsonElement body, int statusCode = 200) =>
        new()
        {
            StatusCode = statusCode,
            ContentType = "application/json",
            JsonBody = body.Clone(),
        };

    public static ExtensionEndpointResponse FromStream(
        IAsyncEnumerable<ReadOnlyMemory<byte>> body,
        string contentType = "application/octet-stream",
        int statusCode = 200
    ) =>
        new()
        {
            StatusCode = statusCode,
            ContentType = contentType,
            BodyStream = body,
        };
}
