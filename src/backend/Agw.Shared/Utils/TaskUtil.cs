using System.Diagnostics;

namespace Agw.Shared.Utils;

public class TaskUtil
{
    public static string GenContextId()
    {
        var contextId = Activity.Current?.TraceId.ToString();
        contextId ??= Guid.NewGuid().Normalize();
        return contextId;
    }
}
