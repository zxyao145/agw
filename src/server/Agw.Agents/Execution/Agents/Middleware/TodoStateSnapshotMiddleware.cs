using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class TodoStateSnapshotMiddleware
{
    private static readonly IReadOnlySet<string> MutationToolNames = new HashSet<string>(
        ["todos_add", "todos_complete", "todos_remove"],
        StringComparer.OrdinalIgnoreCase
    );

    private readonly TodoProvider _provider;

    public TodoStateSnapshotMiddleware(TodoProvider provider)
    {
        _provider = provider;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var inputMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        RegisterFunctionCalls(inputMessages.SelectMany(static message => message.Contents), callNames);
        var snapshottedCallIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (
            var update in innerAgent
                .RunStreamingAsync(inputMessages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            RegisterFunctionCalls(update.Contents, callNames);
            yield return update;

            if (session == null)
            {
                continue;
            }

            foreach (var result in update.Contents.OfType<FunctionResultContent>())
            {
                if (
                    result.Exception != null
                    || !callNames.TryGetValue(result.CallId, out var toolName)
                    || !MutationToolNames.Contains(toolName)
                    || !snapshottedCallIds.Add(result.CallId)
                )
                {
                    continue;
                }

                var snapshot = await ToolStateSnapshots.CreateTodoAsync(
                    _provider,
                    session,
                    toolName,
                    result.CallId,
                    cancellationToken
                );
                yield return ToolStateSnapshots.ToUpdate(snapshot);
            }
        }
    }

    private static void RegisterFunctionCalls(IEnumerable<AIContent> contents, IDictionary<string, string> callNames)
    {
        foreach (var content in contents)
        {
            var call = content switch
            {
                FunctionCallContent functionCall => functionCall,
                ToolApprovalRequestContent { ToolCall: FunctionCallContent functionCall } => functionCall,
                ToolApprovalResponseContent { ToolCall: FunctionCallContent functionCall } => functionCall,
                AlwaysApproveToolApprovalResponseContent { InnerResponse.ToolCall: FunctionCallContent functionCall } =>
                    functionCall,
                _ => null,
            };

            if (
                call is not null
                && !call.InformationalOnly
                && !string.IsNullOrWhiteSpace(call.CallId)
                && !string.IsNullOrWhiteSpace(call.Name)
            )
            {
                callNames[call.CallId] = call.Name;
            }
        }
    }
}
