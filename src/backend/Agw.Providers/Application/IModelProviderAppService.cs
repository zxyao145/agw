using Agw.Domain.Entities;
using Agw.Manager.Api.Contracts;

namespace Agw.Providers.Application;

public interface IModelProviderAppService
{
    Task<IReadOnlyList<ModelProvider>> ListAsync(Guid? modelId = null, Guid? providerId = null);
    Task<ModelProvider?> GetAsync(Guid id);
    Task<ModelProvider> CreateAsync(ModelProviderCreateRequest request, string user);
    Task<ModelProvider?> UpdateAsync(Guid id, ModelProviderUpdateRequest request, string user);
    Task<bool> DeleteAsync(Guid id);
}
