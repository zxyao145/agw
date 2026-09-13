using System.Linq.Expressions;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Projects.Application;

public interface IProjectAppService
{
    Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null);

    Task<IReadOnlyList<Project>> ListForCurrentUserAsync();

    Task<string?> GetProjectExtraSettingAsync(Guid? projectId);

    Task<Guid?> ResolveProjectIdAsync(Guid? projectId);

    Task<Project?> CreateAsync(Project project);

    Task<Project?> CreateAsync(
        Project project,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds
    ) => CreateAsync(project);

    Task<bool> DeleteAsync(Guid id);

    Task<Project?> GetAsync(Guid id);

    Task<Project?> GetForCurrentUserAsync(Guid id);

    async Task<string?> GetOwnerUserIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await GetForCurrentUserAsync(id).ConfigureAwait(false))?.CreateBy;
    }

    Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction);

    Task<Project?> UpdateAsync(
        Guid id,
        Action<Project> updateAction,
        IEnumerable<Guid>? mcpToolServerIds,
        IEnumerable<Guid>? skillIds,
        IEnumerable<Guid>? connectionIds
    ) => UpdateAsync(id, updateAction);
}
