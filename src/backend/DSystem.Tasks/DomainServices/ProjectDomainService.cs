using DSystem.Domain.Repositories;
using DSystem.Shared.Tasks.Entities;
using System.Linq.Expressions;

namespace DSystem.Domain.Services;

public class ProjectDomainService
{
    private readonly IRepository<Project> _projectRepository;
    private readonly IUnitOfWork _unitOfWork;

    public ProjectDomainService(IRepository<Project> projectRepository, IUnitOfWork unitOfWork)
    {
        _projectRepository = projectRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null) =>
        _projectRepository.ListAsync(predicate);

    public Task<Project?> GetAsync(Guid id) => _projectRepository.GetByIdAsync(id);

    public async Task<Project?> CreateAsync(Project project, string user)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
        {
            return null;
        }

        project.Id = project.Id == Guid.Empty ? Guid.NewGuid() : project.Id;
        project.CreateBy = user;
        project.CreateTime = DateTime.UtcNow;
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

        updateAction(existing);

        if (string.IsNullOrWhiteSpace(existing.Name))
        {
            return null;
        }

        existing.UpdateBy = user;
        existing.UpdateTime = DateTime.UtcNow;
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
}
