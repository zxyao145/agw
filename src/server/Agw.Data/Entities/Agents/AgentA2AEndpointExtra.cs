using System.Text.Json;

namespace Agw.Shared.Data.Entities.Agents;

public sealed record AgentA2AEndpointExtra
{
    public JsonElement Card { get; init; }
    public DateTimeOffset CardFetchedAt { get; init; }
}
