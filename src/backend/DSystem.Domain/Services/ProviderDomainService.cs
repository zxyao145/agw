using DSystem.Domain.Entities;
using DSystem.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace DSystem.Domain.Services;

public class ProviderDomainService
{
    private readonly IRepository<Provider> _providerRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ProviderDomainService(IRepository<Provider> providerRepository, IUnitOfWork unitOfWork)
    {
        _providerRepository = providerRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<Provider> CreateAsync(Provider provider, string user)
    {
        provider.Id = provider.Id == Guid.Empty ? Guid.NewGuid() : provider.Id;
        provider.CreateBy = user;
        provider.CreateTime = DateTime.UtcNow;

        foreach (var authConfig in provider.AuthConfigs)
        {
            authConfig.Id = authConfig.Id == Guid.Empty ? Guid.NewGuid() : authConfig.Id;
            authConfig.ProviderId = provider.Id;
            authConfig.CreateBy = user;
            authConfig.CreateTime = DateTime.UtcNow;
            authConfig.UpdateBy = user;
            authConfig.UpdateTime = DateTime.UtcNow;
        }

        await _providerRepository.AddAsync(provider);
        await _unitOfWork.SaveChangesAsync();
        return provider;
    }

    public async Task<Provider?> UpdateAsync(Guid id, Action<Provider> updateAction, string user)
    {
        var existing = await _providerRepository.Queryable
            .Include(p => p.AuthConfigs)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
        _providerRepository.Update(existing);
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

    public async Task<IReadOnlyList<Provider>> ListAsync() => await _providerRepository.ListAsync(null, p => p.AuthConfigs);

    public Task<Provider?> GetAsync(Guid id) => _providerRepository.Queryable
        .Include(p => p.AuthConfigs)
        .FirstOrDefaultAsync(p => p.Id == id);
}
