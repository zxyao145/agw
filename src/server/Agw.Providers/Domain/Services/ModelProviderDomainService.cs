using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.Services;

public class ModelProviderDomainService
{
    private readonly TimeProvider _timeProvider;

    public ModelProviderDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(ModelProviderRelation entity, string user)
    {
        entity.Id = Guid.NewGuid();
        entity.CreateBy = user;
        entity.CreateTime = _timeProvider.GetUtcNow();
    }

    public void ApplyUpdate(ModelProviderRelation entity, Action<ModelProviderRelation> updateAction, string user)
    {
        updateAction(entity);
        entity.UpdateBy = user;
        entity.UpdateTime = _timeProvider.GetUtcNow();
    }
}
