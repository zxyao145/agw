using Agw.Agents.ExternalAgents.Acp;

namespace Agw.Agents.Application.Acp;

public interface IAcpRegistryClient
{
    Task<AcpRegistrySnapshot> GetAsync(bool refresh, CancellationToken ct);
    Task<AcpRegistrySnapshot> FetchAsync(CancellationToken ct);
}

public sealed record AcpRegistrySnapshot(AcpRegistryDocument Document, DateTimeOffset FetchedAt, Uri SourceUrl);
