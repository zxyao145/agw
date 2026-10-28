using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.Protocol;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

internal sealed class CodexProviderSessionBinding
{
    private readonly Func<string, CancellationToken, ValueTask>? _onProviderSessionStartedAsync;
    private readonly bool _isResume;
    private int _captured;

    public CodexProviderSessionBinding(
        Func<string, CancellationToken, ValueTask>? onProviderSessionStartedAsync,
        bool isResume
    )
    {
        _onProviderSessionStartedAsync = onProviderSessionStartedAsync;
        _isResume = isResume;
        _captured = isResume || onProviderSessionStartedAsync == null ? 1 : 0;
    }

    public async ValueTask OnResponseUpdateAsync(AgentResponseUpdate update, CancellationToken cancellationToken)
    {
        if (
            _onProviderSessionStartedAsync == null
            || _isResume
            || Volatile.Read(ref _captured) != 0
            || !TryGetSemanticThreadId(update, out var threadId)
            || Interlocked.CompareExchange(ref _captured, 1, 0) != 0
        )
        {
            return;
        }

        if (!Guid.TryParse(threadId, out var parsedThreadId) || parsedThreadId == Guid.Empty)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "The Codex app-server returned an invalid thread ID.");
        }

        await _onProviderSessionStartedAsync(parsedThreadId.Normalize(), CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryGetSemanticThreadId(AgentResponseUpdate update, out string threadId)
    {
        threadId = string.Empty;
        if (
            update.RawRepresentation is AppServerNotification notification
            && notification.ThreadId is { Length: > 0 } rawThreadId
        )
        {
            if (
                notification.Parameters
                is ItemStartedNotification { Item: not UserMessageThreadItem }
                    or ItemCompletedNotification { Item: not UserMessageThreadItem }
            )
            {
                threadId = rawThreadId;
                return true;
            }

            if (notification.Parameters is TurnCompletedNotification { Turn.Status: TurnStatus.Completed })
            {
                threadId = rawThreadId;
                return true;
            }
        }

        if (update.Contents.Any(IsSemanticContent))
        {
            threadId = (update.RawRepresentation as AppServerNotification)?.ThreadId ?? string.Empty;
            return threadId.Length > 0;
        }

        return false;
    }

    private static bool IsSemanticContent(AIContent content) =>
        content switch
        {
            TextContent { Text.Length: > 0 } => true,
            TextReasoningContent { Text.Length: > 0 } => true,
            FunctionCallContent => true,
            FunctionResultContent => true,
            DataContent => true,
            UriContent => true,
            _ => false,
        };
}
