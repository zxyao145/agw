using System.Text.Json;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;

namespace Agw.Agents.Application.A2A;

public sealed record A2AResolvedCard(JsonElement Card, A2ASelectedInterface Interface, bool Streaming);

public interface IA2AAgentCardClient
{
    Task<A2AResolvedCard> FetchAsync(
        Uri cardUrl,
        A2AEndpointCredentials credentials,
        CancellationToken cancellationToken
    );
}
