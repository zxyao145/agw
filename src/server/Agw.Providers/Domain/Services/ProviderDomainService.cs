using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.Services;

public class ProviderDomainService
{
    private readonly TimeProvider _timeProvider;

    public ProviderDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(Provider provider, string user)
    {
        var now = _timeProvider.GetUtcNow();
        provider.Id = provider.Id == Guid.Empty ? Guid.CreateVersion7() : provider.Id;
        provider.CreateBy = user;
        provider.CreateTime = now;

        NormalizeAuthConfigs(provider.AuthConfigs, provider.Id, user, now);
    }

    public void ApplyUpdate(Provider provider, Action<Provider> updateAction, string user)
    {
        updateAction(provider);

        var now = _timeProvider.GetUtcNow();
        provider.UpdateBy = user;
        provider.UpdateTime = now;
        NormalizeAuthConfigs(provider.AuthConfigs, provider.Id, user, now);
    }

    private static void NormalizeAuthConfigs(
        ICollection<ProviderAuthConfig>? authConfigs,
        Guid providerId,
        string user,
        DateTimeOffset now)
    {
        if (authConfigs == null)
        {
            return;
        }

        foreach (var authConfig in authConfigs)
        {
            authConfig.ProviderId = providerId;
            authConfig.CreateBy ??= user;
            authConfig.CreateTime = authConfig.CreateTime == default ? now : authConfig.CreateTime;
            authConfig.UpdateBy = user;
            authConfig.UpdateTime = now;
        }
    }
}
