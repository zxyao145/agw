using Agw.Shared.Utils;

namespace Agw.Agents.Application.AgentRun;

internal static class ExecutionContextIdResolver
{
    public static string Resolve(string? contextId, string? _)
    {
        return string.IsNullOrWhiteSpace(contextId)
            ? TaskUtil.GenContextId()
            : contextId.Trim();
    }
}
