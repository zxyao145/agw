using DSystem.Shared.Tasks.Entities;

namespace DSystem.Shared.Tasks;

public interface IProjectAppService
{
    Task<string?> GetProjectExtraSettingAsync(string? projectId);
}
