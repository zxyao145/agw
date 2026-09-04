namespace Agw.Shared.Coordination;

public static class ProjectLifecycleLock
{
    public static string GetResourceName(Guid projectId) => $"project-lifecycle:{projectId:D}";
}
