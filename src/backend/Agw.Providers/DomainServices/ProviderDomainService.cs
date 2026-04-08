using Agw.Domain.Entities;

namespace Agw.Domain.Services;

public class ProviderDomainService
{
    public void PrepareForCreate(Provider provider, string user)
    {
        var now = DateTime.UtcNow;
        provider.Id = provider.Id == Guid.Empty ? Guid.NewGuid() : provider.Id;
        provider.CreateBy = user;
        provider.CreateTime = now;

        NormalizeAuthConfigs(provider.AuthConfigs, provider.Id, user, now);
    }

    public void ApplyUpdate(Provider provider, Action<Provider> updateAction, string user)
    {
        updateAction(provider);

        var now = DateTime.UtcNow;
        provider.UpdateBy = user;
        provider.UpdateTime = now;
        NormalizeAuthConfigs(provider.AuthConfigs, provider.Id, user, now);
    }

    private static void NormalizeAuthConfigs(
        ICollection<ProviderAuthConfig>? authConfigs,
        Guid providerId,
        string user,
        DateTime now)
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
