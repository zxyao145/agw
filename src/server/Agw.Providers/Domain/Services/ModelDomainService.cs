using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Domain.Services;

public class ModelDomainService
{
    private readonly TimeProvider _timeProvider;

    public ModelDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(LlmModel model, string user)
    {
        model.Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
        model.CreateBy = user;
        model.CreateTime = _timeProvider.GetUtcNow();
    }

    public void ApplyUpdate(LlmModel model, Action<LlmModel> updateAction, string user)
    {
        updateAction(model);
        model.UpdateBy = user;
        model.UpdateTime = _timeProvider.GetUtcNow();
    }
}
