namespace Agw.Shared.Coordination;

public static class DurableExecutionLock
{
    public static string GetResourceName(Guid executionId) => $"distributed-execution:{executionId:N}";
}
