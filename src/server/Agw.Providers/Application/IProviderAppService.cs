using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;

namespace Agw.Providers.Application;

public interface IProviderAppService
{
    Task<IReadOnlyList<Provider>> ListAsync();

    Task<Provider?> GetAsync(Guid id);

    Task<Provider> CreateAsync(ProviderCreateRequest request, CancellationToken cancellationToken = default);

    Task<Provider?> UpdateAsync(Guid id, ProviderUpdateRequest request, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
