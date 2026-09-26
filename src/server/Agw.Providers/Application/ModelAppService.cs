using Agw.Providers.Application.Persistence;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Domain.Behaviors;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Microsoft.EntityFrameworkCore;

namespace Agw.Providers.Application;

public class ModelAppService : IModelAppService
{
    private readonly IProvidersDbContext _dbContext;
    private readonly ICurrentUser _currentUser;

    public ModelAppService(IProvidersDbContext dbContext, ICurrentUser currentUser)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<AgwAiModel>> ListAsync()
    {
        var ownerUserId = ResolveOwnerUserId();
        return await _dbContext.Models.AsNoTracking().Where(model => model.CreateBy == ownerUserId).ToListAsync();
    }

    public Task<AgwAiModel?> GetAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        return _dbContext
            .Models.AsNoTracking()
            .FirstOrDefaultAsync(model => model.Id == id && model.CreateBy == ownerUserId);
    }

    public async Task<AgwAiModel> CreateAsync(ModelCreateRequest request)
    {
        _ = ResolveOwnerUserId();
        var model = new AgwAiModel { Id = Guid.CreateVersion7() };
        new AgwAiModelBehavior(model).Define(
            request.Name,
            request.Description,
            request.MaxContextWindowTokens,
            request.MaxOutputTokens
        );

        await _dbContext.Models.AddAsync(model);
        await _dbContext.SaveChangesAsync();
        return model;
    }

    public async Task<AgwAiModel?> UpdateAsync(Guid id, ModelUpdateRequest request)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Models.FirstOrDefaultAsync(model =>
            model.Id == id && model.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return null;
        }

        new AgwAiModelBehavior(existing).Define(
            request.Name,
            request.Description,
            request.MaxContextWindowTokens,
            request.MaxOutputTokens
        );

        _dbContext.Models.Entry(existing).Property(model => model.Name).IsModified = true;
        await _dbContext.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var ownerUserId = ResolveOwnerUserId();
        var existing = await _dbContext.Models.FirstOrDefaultAsync(model =>
            model.Id == id && model.CreateBy == ownerUserId
        );
        if (existing == null)
        {
            return false;
        }

        _dbContext.Models.Remove(existing);
        await _dbContext.SaveChangesAsync();
        return true;
    }

    private string ResolveOwnerUserId() => _currentUser.RequiredUserId;
}
