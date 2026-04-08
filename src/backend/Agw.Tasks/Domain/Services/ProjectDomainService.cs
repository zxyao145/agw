using Agw.Shared.Tasks.Entities;

namespace Agw.Tasks.Domain.Services;

public class ProjectDomainService
{
    public bool TryPrepareForCreate(Project project, string user)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
        {
            return false;
        }

        project.Id = project.Id == Guid.Empty ? Guid.NewGuid() : project.Id;
        project.CreateBy = user;
        project.CreateTime = DateTime.UtcNow;
        return true;
    }

    public bool TryApplyUpdate(Project project, Action<Project> updateAction, string user)
    {
        updateAction(project);

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            return false;
        }

        project.UpdateBy = user;
        project.UpdateTime = DateTime.UtcNow;
        return true;
    }
}
