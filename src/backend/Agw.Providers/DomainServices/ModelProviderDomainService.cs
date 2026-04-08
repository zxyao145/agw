using Agw.Domain.Entities;

namespace Agw.Domain.Services;

public class ModelProviderDomainService
{
    public void PrepareForCreate(ModelProviderRelation entity, string user)
    {
        entity.Id = Guid.NewGuid();
        entity.CreateBy = user;
        entity.CreateTime = DateTime.UtcNow;
    }

    public void ApplyUpdate(ModelProviderRelation entity, Action<ModelProviderRelation> updateAction, string user)
    {
        updateAction(entity);
        entity.UpdateBy = user;
        entity.UpdateTime = DateTime.UtcNow;
    }
}
