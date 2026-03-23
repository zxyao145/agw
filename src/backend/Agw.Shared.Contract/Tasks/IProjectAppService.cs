using Agw.Shared.Tasks.Entities;
using System.Linq.Expressions;

namespace Agw.Shared.Tasks;

public interface IProjectAppService
{
    Task<IReadOnlyList<Project>> ListAsync(Expression<Func<Project, bool>>? predicate = null);

    Task<string?> GetProjectExtraSettingAsync(Guid? projectId);

    Task<Guid?> ResolveProjectIdAsync(Guid? projectId);
}
