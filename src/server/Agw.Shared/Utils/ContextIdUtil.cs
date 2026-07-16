using System.Diagnostics;

using Agw.Shared.Extensions;

namespace Agw.Shared.Utils;

public class ContextIdUtil
{
    /// <summary>
    /// 优先使用当前 Activity 的 TraceId 生成 context ID；无活动时生成新的 GUID。
    /// </summary>
    public static string GenContextId()
    {
        var contextId = Activity.Current?.TraceId.ToString();
        contextId ??= Guid.CreateVersion7().ToString("N");
        return contextId;
    }

    /// <summary>
    /// 解析可选 context ID；空值生成新标识，非空值转换为规范格式。
    /// </summary>
    public static string ResolveContextId(string? contextId)
    {
        var resolvedContextId = string.IsNullOrWhiteSpace(contextId)
            ? GenContextId()
            : contextId;

        return NormalizeContextId(resolvedContextId);
    }

    /// <summary>
    /// 去除 context ID 首尾空白，并将可解析的 GUID 转换为统一的 D 格式。
    /// </summary>
    public static string NormalizeContextId(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        var normalizedContextId = contextId.Trim();
        return Guid.TryParse(normalizedContextId, out var guidContextId)
            ? guidContextId.Normalize()
            : normalizedContextId;
    }
}
