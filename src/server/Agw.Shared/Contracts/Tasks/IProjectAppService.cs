using System.Linq.Expressions;

using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Contracts.Tasks;

public interface IProjectAppService
{
    Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null);

    Task<string?> GetProjectExtraSettingAsync(Guid? projectId);

    Task<Guid?> ResolveProjectIdAsync(Guid? projectId);

    Task<Project?> CreateAsync(Project project, string user);

    Task<bool> DeleteAsync(Guid id);

    Task<Project?> GetAsync(Guid id);

    Task<Project?> UpdateAsync(Guid id, Action<Project> updateAction, string user);
}
