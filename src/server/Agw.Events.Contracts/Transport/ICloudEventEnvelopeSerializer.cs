using Agw.Shared.Contracts.Events;
using Microsoft.AspNetCore.Http;

namespace Agw.Events.Contracts.Transport;

public interface ICloudEventEnvelopeSerializer
{
    const string BatchContentType = "application/cloudevents-batch+json";

    byte[] Serialize(CloudEventEnvelope message);

    void Validate(CloudEventEnvelope message);

    IReadOnlyList<CloudEventEnvelope> DeserializeBatch(ReadOnlyMemory<byte> body, string? contentType);

    Task<CloudEventEnvelope> DeserializeHttpAsync(HttpRequest request, CancellationToken cancellationToken);
}
