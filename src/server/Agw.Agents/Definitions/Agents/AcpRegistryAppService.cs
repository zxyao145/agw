using Agw.Agents.Application.Acp;
using Agw.Agents.Definitions.Contracts;
using Agw.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Definitions.Agents;

public sealed class AcpRegistryAppService
{
    private readonly IAcpRegistryClient _client;

    public AcpRegistryAppService(IAcpRegistryClient client) => _client = client;

    public async Task<AcpRegistryResponse> GetAsync(bool refresh, CancellationToken ct)
    {
        var snapshot = await _client.GetAsync(refresh, ct);
        return new(snapshot.SourceUrl, snapshot.FetchedAt, snapshot.Document.Agents.Select(ToResponse).ToArray());
    }

    private static AcpRegistryAgentResponse ToResponse(AcpRegistryAgent agent)
    {
        var kinds = new List<string>();
        if (agent.Distribution.Binary is not null)
            kinds.Add(AcpDistributionKinds.Binary);
        if (agent.Distribution.Npx is not null)
            kinds.Add(AcpDistributionKinds.Npx);
        if (agent.Distribution.Uvx is not null)
            kinds.Add(AcpDistributionKinds.Uvx);
        return new(
            agent.Id,
            agent.Name,
            agent.Version,
            agent.Description,
            agent.Repository,
            agent.Website,
            agent.License,
            kinds
        );
    }
}
