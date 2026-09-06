using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.Behaviors;

public sealed class ProviderBehavior
{
    private readonly Provider _provider;

    public ProviderBehavior(Provider provider)
    {
        _provider = provider;
    }

    public void ApplyAuthConfigs(IReadOnlyCollection<ProviderAuthConfig> proposed)
    {
        var proposedById = proposed.Where(config => config.Id != Guid.Empty).ToDictionary(config => config.Id);
        foreach (var existing in _provider.AuthConfigs.ToList())
        {
            if (!proposedById.Remove(existing.Id, out var updated))
            {
                _provider.AuthConfigs.Remove(existing);
                continue;
            }

            existing.ProviderId = _provider.Id;
            existing.AuthType = updated.AuthType;
            existing.ApiKey = updated.ApiKey;
            existing.EnvName = updated.EnvName;
            existing.Enable = updated.Enable;
        }

        foreach (var config in proposed.Where(config => config.Id == Guid.Empty).Concat(proposedById.Values))
        {
            _provider.AuthConfigs.Add(
                new ProviderAuthConfig
                {
                    Id = config.Id,
                    ProviderId = _provider.Id,
                    AuthType = config.AuthType,
                    ApiKey = config.ApiKey,
                    EnvName = config.EnvName,
                    Enable = config.Enable,
                }
            );
        }
    }
}
