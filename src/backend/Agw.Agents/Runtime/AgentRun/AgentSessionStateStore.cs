using System.Text.Json;

using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Runtime.AgentRun;

public sealed class AgentSessionStateStore
{
    private readonly HybridCache  _cache;
    private readonly ILogger<AgentSessionStateStore> _logger;

    public AgentSessionStateStore(HybridCache cache, ILogger<AgentSessionStateStore> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<AgentSession> GetOrCreateAsync(
        Agent agent,
        AIAgent aiAgent,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        if (agent.Type == AgentType.External)
        {
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        var serialized = await _cache.GetOrCreateAsync(
            sessionKey,
            _ => ValueTask.FromResult(string.Empty),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(serialized))
        {
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var serializedSession = JsonSerializer.Deserialize<JsonElement>(serialized);
            return await aiAgent.DeserializeSessionAsync(serializedSession, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Agent session cache deserialization failed for session {SessionKey}. A new session will be created.",
                sessionKey);
            return await aiAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(
        string sessionKey,
        AIAgent aiAgent,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var serializedSession = await aiAgent.SerializeSessionAsync(session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var serialized = JsonSerializer.Serialize(serializedSession);
        await _cache.SetAsync(sessionKey, serialized, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
