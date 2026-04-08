using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Entities;
using Agw.Providers.Domain.Services;
using Agw.Shared.Abstractions.Repositories;

namespace Agw.Providers.Application;

public class ModelAppService : IModelAppService
{
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ModelDomainService _modelDomainService;

    public ModelAppService(
        IRepository<LlmModel> modelRepository,
        IUnitOfWork unitOfWork,
        ModelDomainService modelDomainService)
    {
        _modelRepository = modelRepository;
        _unitOfWork = unitOfWork;
        _modelDomainService = modelDomainService;
    }

    public Task<IReadOnlyList<LlmModel>> ListAsync() => _modelRepository.ListAsync();

    public Task<LlmModel?> GetAsync(Guid id) => _modelRepository.GetByIdAsync(id);

    public async Task<LlmModel> CreateAsync(ModelCreateRequest request, string user)
    {
        var model = new LlmModel
        {
            Name = request.Name,
            Description = request.Description,
            Type = request.Type,
            MaxTokens = request.MaxTokens
        };

        _modelDomainService.PrepareForCreate(model, user);
        await _modelRepository.AddAsync(model);
        await _unitOfWork.SaveChangesAsync();
        return model;
    }

    public async Task<LlmModel?> UpdateAsync(Guid id, ModelUpdateRequest request, string user)
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
            model.Type = request.Type;
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
