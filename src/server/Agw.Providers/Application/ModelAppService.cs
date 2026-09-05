using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Rules;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ModelAppService : IModelAppService
{
    private readonly IRepository<AgwAiModel> _modelRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public ModelAppService(IRepository<AgwAiModel> modelRepository, IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _modelRepository = modelRepository;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public Task<IReadOnlyList<AgwAiModel>> ListAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        return _modelRepository.ListAsync(model => model.CreateBy == ownerUserId);
    }

    public Task<AgwAiModel?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _modelRepository.Queryable.FirstOrDefaultAsync(model => model.Id == id && model.CreateBy == ownerUserId);
    }

    public async Task<AgwAiModel> CreateAsync(ModelCreateRequest request, string user)
    {
        _ = ResolveOwnerUserId();
        ModelRules.ValidateTokenLimits(request.MaxContextWindowTokens, request.MaxOutputTokens);
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            Description = request.Description,
            MaxContextWindowTokens = request.MaxContextWindowTokens,
            MaxOutputTokens = request.MaxOutputTokens,
        };

        await _modelRepository.AddAsync(model);
        await _unitOfWork.SaveChangesAsync();
        return model;
    }

    public async Task<AgwAiModel?> UpdateAsync(Guid id, ModelUpdateRequest request, string user)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _modelRepository.Queryable.FirstOrDefaultAsync(model =>
            model.Id == id && model.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return null;
        }

        ModelRules.ValidateTokenLimits(request.MaxContextWindowTokens, request.MaxOutputTokens);
        existing.Name = request.Name;
        existing.Description = request.Description;
        existing.MaxContextWindowTokens = request.MaxContextWindowTokens;
        existing.MaxOutputTokens = request.MaxOutputTokens;

        _modelRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _modelRepository.Queryable.FirstOrDefaultAsync(model =>
            model.Id == id && model.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return false;
        }

        _modelRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
