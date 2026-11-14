using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;

namespace Agw.Events.Application;

internal static class EventManagedRecordGuard
{
    /// <summary>
    /// <para>Events API 只能修改 managed_by 为 user 的记录，其他记录由它的管理方修改。</para>
    /// <para>The Events API edits only records whose managed_by is user; other records are edited by their manager.</para>
    /// </summary>
    public static void EnsureUserManaged(string managedBy, ErrorCode error, string recordName)
    {
        if (managedBy == EventManagedBy.User)
        {
            return;
        }

        var manager = managedBy == EventManagedBy.Job ? "a Job" : "an extension";
        throw new AgwException(error, $"{recordName} is managed by {manager}.");
    }
}
