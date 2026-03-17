using Agw.Shared.Tasks.Entities;

namespace Agw.Shared.Tasks;

public interface IProjectAppService
{
    Task<string?> GetProjectExtraSettingAsync(string? projectId);
}
