using Agw.Domain.Repositories;
using Agw.Domain.Services;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using System.Linq.Expressions;

namespace Agw.Tasks.Services;

public class ProjectAppService : IProjectAppService
{
    private readonly IRepository<Project> _projectRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ProjectDomainService _projectDomainService;

    public ProjectAppService(
        IRepository<Project> projectRepository,
        IUnitOfWork unitOfWork,
        ProjectDomainService projectDomainService)
    {
        _projectRepository = projectRepository;
        _unitOfWork = unitOfWork;
        _projectDomainService = projectDomainService;
    }

    public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
        _projectRepository.ListAsync(predicate);

    public Task<Project?> GetAsync(Guid id) => _projectRepository.GetByIdAsync(id);

    public async Task<Project?> CreateAsync(Project project, string user)
    {
        if (!_projectDomainService.TryPrepareForCreate(project, user))
        {
            return null;
        }

        await _projectRepository.AddAsync(project);
        await _unitOfWork.SaveChangesAsync();
        return project;
    }

    public async Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user)
    {
        var existing = await _projectRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return null;
        }

        if (!_projectDomainService.TryApplyUpdate(existing, updateAction, user))
        {
            return null;
        }

        _projectRepository.Update(existing);
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        var existing = await _projectRepository.GetByIdAsync(id);
        if (existing == null)
        {
            return false;
        }

        _projectRepository.Remove(existing);
        await _unitOfWork.SaveChangesAsync();
        return true;
    }

    public async Task<string?> GetProjectExtraSettingAsync(string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        if (!Guid.TryParse(projectId, out var projectGuid))
        {
            return null;
        }

        var project = await _projectRepository.GetByIdAsync(projectGuid);
        return project?.ExtraSetting;
    }
}
