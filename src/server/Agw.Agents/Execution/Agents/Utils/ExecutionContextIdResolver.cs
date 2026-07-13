using Agw.Shared.Extensions;
using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Agents.Utils;

internal static class ExecutionContextIdResolver
{
    public static string Resolve(string? contextId)
    {
        var resolvedContextId = string.IsNullOrWhiteSpace(contextId)
            ? TaskUtil.GenContextId()
            : contextId.Trim();

        return Guid.TryParse(resolvedContextId, out var guidContextId)
            ? guidContextId.Normalize()
            : resolvedContextId;
    }
}
