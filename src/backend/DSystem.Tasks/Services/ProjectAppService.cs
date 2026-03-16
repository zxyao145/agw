using DSystem.Domain.Repositories;
using DSystem.Shared.Tasks;
using DSystem.Shared.Tasks.Entities;

namespace DSystem.Tasks.Services;

public class ProjectAppService : IProjectAppService
{
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<ProjectTask> _taskRepository;

    public ProjectAppService(IRepository<Project> projectRepository, IRepository<ProjectTask> taskRepository)
    {
        _projectRepository = projectRepository;
        _taskRepository = taskRepository;
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
