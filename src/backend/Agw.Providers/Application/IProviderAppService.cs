using Agw.Domain.Entities;
using Agw.Manager.Api.Contracts;

namespace Agw.Providers.Application;

public interface IProviderAppService
{
    Task<IReadOnlyList<Provider>> ListAsync();
    Task<Provider?> GetAsync(Guid id);
    Task<Provider> CreateAsync(ProviderCreateRequest request, string user);
    Task<Provider?> UpdateAsync(Guid id, ProviderUpdateRequest request, string user);
    Task<bool> DeleteAsync(Guid id);
}
