using Agw.Domain.Entities;
using Agw.Manager.Api.Contracts;

namespace Agw.Providers.Application;

public interface IModelAppService
{
    Task<IReadOnlyList<LlmModel>> ListAsync();
    Task<LlmModel?> GetAsync(Guid id);
    Task<LlmModel> CreateAsync(ModelCreateRequest request, string user);
    Task<LlmModel?> UpdateAsync(Guid id, ModelUpdateRequest request, string user);
    Task<bool> DeleteAsync(Guid id);
}
