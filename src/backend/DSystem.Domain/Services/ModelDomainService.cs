using DSystem.Domain.Entities;
using DSystem.Shared.Repositories;

namespace DSystem.Domain.Services;

public class ModelDomainService
{
    private readonly IRepository<LlmModel> _modelRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ModelDomainService(IRepository<LlmModel> modelRepository, IUnitOfWork unitOfWork)
    {
        _modelRepository = modelRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<LlmModel> CreateAsync(LlmModel model, string user)
    {
        model.Id = model.Id == Guid.Empty ? Guid.NewGuid() : model.Id;
        model.CreateBy = user;
        model.CreateTime = DateTime.UtcNow;
        await _modelRepository.AddAsync(model);
        await _unitOfWork.SaveChangesAsync();
        return model;
    }

    public async Task<LlmModel?> UpdateAsync(Guid id, Action<LlmModel> updateAction, string user)
    {
        var existing = await _modelRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        updateAction(existing);
        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
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

    public Task<IReadOnlyList<LlmModel>> ListAsync() => _modelRepository.ListAsync();

    public Task<LlmModel?> GetAsync(Guid id) => _modelRepository.GetByIdAsync(id);
}
