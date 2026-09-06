using System.Linq.Expressions;
using Agw.Shared.Data.Entities.Executions;

namespace Agw.Agents.Application.Persistence;

public static class DurableExecutionQueries
{
    private static readonly DurableExecutionStatus[] TerminalStatuses =
    [
        DurableExecutionStatus.Completed,
        DurableExecutionStatus.Failed,
        DurableExecutionStatus.Interrupted,
    ];

    public static bool IsTerminal(DurableExecutionStatus status) => TerminalStatuses.Contains(status);

    public static Expression<Func<DurableExecutionRecord, bool>> Active { get; } =
        item => !TerminalStatuses.Contains(item.Status);

    public static IQueryable<DurableExecutionRecord> InConversation(
        this IQueryable<DurableExecutionRecord> query,
        Guid projectId,
        Guid conversationId,
        string ownerUserId
    ) =>
        query.Where(item =>
            item.UserId == ownerUserId && item.ProjectId == projectId && item.ProjectConversationId == conversationId
        );
}
