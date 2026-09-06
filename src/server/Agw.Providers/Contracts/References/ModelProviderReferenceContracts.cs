using System.Text.Json.Serialization;
using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Contracts.References;

public sealed record ModelProviderModelSnapshot(Guid Id, string Name, int MaxContextWindowTokens, int MaxOutputTokens);

public sealed class ProviderAuthConfigSnapshot
{
    public ProviderAuthConfigSnapshot(bool enable, string? apiKey)
    {
        Enable = enable;
        ApiKey = apiKey;
    }

    public bool Enable { get; }

    [JsonIgnore]
    public string? ApiKey { get; }

    public override string ToString() => $"{nameof(ProviderAuthConfigSnapshot)} {{ Enable = {Enable}, ApiKey = *** }}";
}

public sealed record ModelProviderProviderSnapshot(
    Guid Id,
    string Name,
    ProviderType ProviderType,
    string Endpoint,
    IReadOnlyList<ProviderAuthConfigSnapshot> AuthConfigs
);

public sealed record ModelProviderRuntimeSnapshot(
    Guid Id,
    ModelProviderModelSnapshot Model,
    ModelProviderProviderSnapshot Provider
);

public interface IModelProviderReferenceFacade
{
    Task<IReadOnlySet<Guid>> FilterVisibleModelProviderIdsAsync(
        IReadOnlyCollection<Guid> modelProviderIds,
        CancellationToken cancellationToken = default
    );

    Task<ModelProviderRuntimeSnapshot?> GetRuntimeSnapshotAsync(
        Guid modelProviderId,
        CancellationToken cancellationToken = default
    );
}
