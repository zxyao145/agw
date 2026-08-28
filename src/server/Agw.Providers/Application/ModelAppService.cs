using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ModelAppService : IModelAppService
{
    private readonly IRepository<AgwAiModel> _modelRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ModelDomainService _modelDomainService;
    private readonly ICurrentUser _currentUser;

    public ModelAppService(
        IRepository<AgwAiModel> modelRepository,
        IUnitOfWork unitOfWork,
        ModelDomainService modelDomainService,
        ICurrentUser currentUser
    )
    {
        _modelRepository = modelRepository;
        _unitOfWork = unitOfWork;
        _modelDomainService = modelDomainService;
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
        _modelDomainService.ValidateTokenLimits(request.MaxContextWindowTokens, request.MaxOutputTokens);
        var model = new AgwAiModel
        {
            Name = request.Name,
            Description = request.Description,
            MaxContextWindowTokens = request.MaxContextWindowTokens,
            MaxOutputTokens = request.MaxOutputTokens,
        };

        _modelDomainService.PrepareForCreate(model, user);
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

        _modelDomainService.ValidateTokenLimits(request.MaxContextWindowTokens, request.MaxOutputTokens);
        _modelDomainService.ApplyUpdate(
            existing,
            model =>
            {
                model.Name = request.Name;
                model.Description = request.Description;
                model.MaxContextWindowTokens = request.MaxContextWindowTokens;
                model.MaxOutputTokens = request.MaxOutputTokens;
            },
            user
        );

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
