using Agw.Shared.Contracts.Events;

namespace Agw.Events.Contracts.Transport;

public interface ICloudEventEnvelopeSerializer
{
    byte[] Serialize(CloudEventEnvelope message);

    void Validate(CloudEventEnvelope message);
}
