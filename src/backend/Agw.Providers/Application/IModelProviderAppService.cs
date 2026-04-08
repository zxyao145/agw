using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Entities;

namespace Agw.Providers.Application;

public interface IModelProviderAppService
{
    Task<IReadOnlyList<ModelProviderRelation>> ListAsync(Guid? modelId = null, Guid? providerId = null);

    Task<ModelProviderRelation?> GetAsync(Guid id);

    Task<ModelProviderRelation> CreateAsync(ModelProviderCreateRequest request, string user);

    Task<ModelProviderRelation?> UpdateAsync(Guid id, ModelProviderUpdateRequest request, string user);

    Task<bool> DeleteAsync(Guid id);
}
