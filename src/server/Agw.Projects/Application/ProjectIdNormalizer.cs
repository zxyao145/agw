using Agw.Shared.Extensions;

namespace Agw.Projects.Application;

internal static class ProjectIdNormalizer
{
    public static string Normalize(string projectId)
    {
        var normalizedProjectId = projectId.Trim();
        return Guid.TryParse(normalizedProjectId, out var parsedProjectId)
            ? parsedProjectId.Normalize()
            : normalizedProjectId;
    }
}
