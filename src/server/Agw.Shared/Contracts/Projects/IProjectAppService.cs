using System.Linq.Expressions;

using Agw.Shared.Data.Entities.Projects;

namespace Agw.Shared.Contracts.Projects;

public interface IProjectAppService
{
    Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null);

    Task<string?> GetProjectExtraSettingAsync(Guid? projectId);

    Task<Guid?> ResolveProjectIdAsync(Guid? projectId);

    Task<Project?> CreateAsync(Project project, string user);

    Task<Project?> CreateAsync(
        Project project,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds,
        string user) => CreateAsync(project, user);

    Task<bool> DeleteAsync(Guid id);

    Task<Project?> GetAsync(Guid id);

    Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user);

    Task<Project?> UpdateAsync(
        Guid id,
        Action<Project> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds,
        string user) => UpdateAsync(id, updateAction, user);
}
