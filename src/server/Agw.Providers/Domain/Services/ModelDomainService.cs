using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

namespace Agw.Providers.Domain.Services;

public class ModelDomainService
{
    private readonly TimeProvider _timeProvider;

    public ModelDomainService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public void PrepareForCreate(AgwAiModel model, string user)
    {
        model.Id = model.Id == Guid.Empty ? Guid.CreateVersion7() : model.Id;
        model.CreateBy = user;
        model.CreateTime = _timeProvider.GetUtcNow();
    }

    public void ValidateTokenLimits(int maxContextWindowTokens, int maxOutputTokens)
    {
        if (maxContextWindowTokens <= 0)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "maxContextWindowTokens must be greater than zero.");
        }

        if (maxOutputTokens <= 0 || maxOutputTokens >= maxContextWindowTokens)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "maxOutputTokens must be greater than zero and less than maxContextWindowTokens.");
        }
    }

    public void ApplyUpdate(AgwAiModel model, Action<AgwAiModel> updateAction, string user)
    {
        updateAction(model);
        model.UpdateBy = user;
        model.UpdateTime = _timeProvider.GetUtcNow();
    }
}
