using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Application;

public interface IModelAppService
{
    Task<IReadOnlyList<AgwAiModel>> ListAsync();

    Task<AgwAiModel?> GetAsync(Guid id);

    Task<AgwAiModel> CreateAsync(ModelCreateRequest request);

    Task<AgwAiModel?> UpdateAsync(Guid id, ModelUpdateRequest request);

    Task<bool> DeleteAsync(Guid id);
}
