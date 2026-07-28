using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Services;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Repositories;

namespace Agw.Providers.Application;

public class ModelAppService : IModelAppService
{
    private readonly IRepository<AgwAiModel> _modelRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ModelDomainService _modelDomainService;

    public ModelAppService(
        IRepository<AgwAiModel> modelRepository,
        IUnitOfWork unitOfWork,
        ModelDomainService modelDomainService)
    {
        _modelRepository = modelRepository;
        _unitOfWork = unitOfWork;
        _modelDomainService = modelDomainService;
    }

    public Task<IReadOnlyList<AgwAiModel>> ListAsync() => _modelRepository.ListAsync();

    public Task<AgwAiModel?> GetAsync(Guid id) => _modelRepository.GetByIdAsync(id);

    public async Task<AgwAiModel> CreateAsync(ModelCreateRequest request, string user)
    {
        var model = new AgwAiModel
        {
            Name = request.Name,
            Description = request.Description,
            MaxTokens = request.MaxTokens
        };

        _modelDomainService.PrepareForCreate(model, user);
        await _modelRepository.AddAsync(model);
        await _unitOfWork.SaveChangesAsync();
        return model;
    }

    public async Task<AgwAiModel?> UpdateAsync(Guid id, ModelUpdateRequest request, string user)
    {
        var existing = await _modelRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        _modelDomainService.ApplyUpdate(existing, model =>
        {
            model.Name = request.Name;
            model.Description = request.Description;
            model.MaxTokens = request.MaxTokens;
        }, user);

        _modelRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _modelRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _modelRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }
}
