using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Agents.Utils;

internal static class ExecutionContextIdResolver
{
    public static string Resolve(string? contextId)
    {
        return string.IsNullOrWhiteSpace(contextId)
            ? TaskUtil.GenContextId()
            : contextId.Trim();
    }
}
