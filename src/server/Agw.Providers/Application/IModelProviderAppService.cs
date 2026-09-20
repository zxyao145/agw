using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Application;

public interface IModelProviderAppService
{
    Task<IReadOnlyList<ModelProviderRelation>> ListAsync(Guid? modelId = null, Guid? providerId = null);

    Task<ModelProviderRelation?> GetAsync(Guid id);

    Task<ModelProviderRelation> CreateAsync(ModelProviderCreateRequest request);

    Task<ModelProviderRelation?> UpdateAsync(Guid id, ModelProviderUpdateRequest request);

    Task<bool> DeleteAsync(Guid id);
}
