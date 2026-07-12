using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.Services;

public class ModelDomainService
{
    public void PrepareForCreate(LlmModel model, string user)
    {
        model.Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
        model.CreateBy = user;
        model.CreateTime = DateTime.UtcNow;
    }

    public void ApplyUpdate(LlmModel model, Action<LlmModel> updateAction, string user)
    {
        updateAction(model);
        model.UpdateBy = user;
        model.UpdateTime = DateTime.UtcNow;
    }
}
