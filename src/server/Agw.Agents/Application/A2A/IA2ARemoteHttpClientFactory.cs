using Agw.Agents.Definitions.Domain.ValueObjects;

namespace Agw.Agents.Application.A2A;

public interface IA2ARemoteHttpClientFactory
{
    A2ARemoteHttpClient Create(Uri targetUri, A2AEndpointCredentials credentials);
}
