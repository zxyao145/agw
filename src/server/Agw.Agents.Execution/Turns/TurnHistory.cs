using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Context;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// TurnExecutor 的历史生命周期：作用域还没有历史缓冲时为解析后的对话建立并绑定一个，记录执行异常，并在流结束时提交自己建立的缓冲。
/// The history lifecycle of a TurnExecutor: binds a buffer for the resolved conversation when the scope has none, records execution failures, and commits the buffer it created when the stream ends.
/// </summary>
internal static class TurnHistory
{
    public static async IAsyncEnumerable<T> RunAsync<T>(
        ExecutionScope scope,
        IConversationHistoryStore? store,
        ConversationHistoryScope conversation,
        IAsyncEnumerable<T> execution,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(conversation);
        IConversationHistoryBuffer? owned = null;
        if (scope.History is { } existing)
        {
            if (
                existing.Scope.ProjectId != conversation.ProjectId
                || !string.Equals(
                    existing.Scope.ContextId,
                    ContextIdUtil.NormalizeContextId(conversation.ContextId),
                    StringComparison.Ordinal
                )
                || existing.Scope.Generation != conversation.Generation
            )
                throw new AgwException(
                    ErrorCodes.ConversationSessionConflict,
                    "The turn already writes history for another conversation."
                );
        }
        else if (store != null)
        {
            owned = store.BeginBuffer(conversation);
            scope.BindHistory(owned);
        }

        try
        {
            await foreach (var item in ObserveAsync(scope, execution, cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            if (owned != null)
                await owned.CompleteAsync(scope.Failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把枚举、推进与释放中的异常记录为执行失败后继续抛出。
    /// Records exceptions from enumeration, advancing and disposal as the execution failure, then rethrows them.
    /// </summary>
    public static async IAsyncEnumerable<T> ObserveAsync<T>(
        ExecutionScope scope,
        IAsyncEnumerable<T> execution,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        IAsyncEnumerator<T> enumerator;
        try
        {
            enumerator = execution.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception)
        {
            scope.RecordFailure(exception);
            throw;
        }
        Exception? failure = null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    scope.RecordFailure(exception);
                    throw;
                }
                if (!hasNext)
                    break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                scope.RecordFailure(exception);
                if (failure == null)
                    throw;
            }
        }
    }
}
