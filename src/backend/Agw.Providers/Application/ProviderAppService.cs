using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Entities;
using Agw.Providers.Domain.Services;
using Agw.Shared.Abstractions.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ProviderAppService : IProviderAppService
{
    private readonly IRepository<Provider> _providerRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProviderDomainService _providerDomainService;

    public ProviderAppService(
        IRepository<Provider> providerRepository,
        IUnitOfWork unitOfWork,
        ProviderDomainService providerDomainService)
    {
        _providerRepository = providerRepository;
        _unitOfWork = unitOfWork;
        _providerDomainService = providerDomainService;
    }

    public Task<IReadOnlyList<Provider>> ListAsync() => _providerRepository.ListAsync(null, e => e.OrderByDescending(x => x.CreateTime), provider => provider.AuthConfigs);

    public Task<Provider?> GetAsync(Guid id) => _providerRepository.Queryable
        .Include(provider => provider.AuthConfigs)
        .FirstOrDefaultAsync(provider => provider.Id == id);

    public async Task<Provider> CreateAsync(ProviderCreateRequest request, string user)
    {
        var provider = new Provider
        {
            Name = request.Name,
            ProviderType = request.ProviderType,
            Description = request.Description,
            Endpoint = request.Endpoint,
            AuthConfigs = BuildAuthConfigs(request.AuthConfigs)
        };

        _providerDomainService.PrepareForCreate(provider, user);
        await _providerRepository.AddAsync(provider);
        await _unitOfWork.SaveChangesAsync();
        return provider;
    }

    public async Task<Provider?> UpdateAsync(Guid id, ProviderUpdateRequest request, string user)
    {
        var existing = await _providerRepository.Queryable
            .Include(provider => provider.AuthConfigs)
            .FirstOrDefaultAsync(provider => provider.Id == id);
        if (existing == null)
        {
            return null;
        }

        _providerDomainService.ApplyUpdate(existing, provider =>
        {
            provider.Name = request.Name;
            provider.ProviderType = request.ProviderType;
            provider.Description = request.Description;
            provider.Endpoint = request.Endpoint;

            if (provider.AuthConfigs == null)
            {
                provider.AuthConfigs = new List<ProviderAuthConfig>();
            }

            provider.AuthConfigs.Clear();
            foreach (var authConfig in BuildAuthConfigs(request.AuthConfigs))
            {
                provider.AuthConfigs.Add(authConfig);
            }
        }, user);

        // `existing` is already tracked from the query above, so SaveChanges will
        // persist both the scalar updates and the auth-config collection changes.
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _providerRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _providerRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private static List<ProviderAuthConfig> BuildAuthConfigs(IReadOnlyList<ProviderAuthConfigRequest>? requests)
    {
        if (requests == null || requests.Count == 0)
        {
            return [];
        }

        return requests.Select(request =>
        {
            var (apiKey, envKey) = request.AuthType switch
            {
                ProviderAuthType.ApiKey => (request.ApiKey, null),
                ProviderAuthType.EnvVariable => (null, request.EnvKey),
                _ => (request.ApiKey, request.EnvKey)
            };

            return new ProviderAuthConfig
            {
                AuthType = request.AuthType,
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
                EnvName = string.IsNullOrWhiteSpace(envKey) ? null : envKey,
                Enable = request.Enable
            };
        }).ToList();
    }
}
