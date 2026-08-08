using System.Runtime.CompilerServices;

using Agw.Agents.Execution.Agents.Tools;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class ModeStateSnapshotMiddleware
{
    private static readonly IReadOnlySet<string> ModeToolNames = new HashSet<string>(
        ["mode_set", "mode_get"],
        StringComparer.OrdinalIgnoreCase);

    private readonly AgentModeProvider _provider;

    public ModeStateSnapshotMiddleware(AgentModeProvider provider)
    {
        _provider = provider;
    }

    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var inputMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var response = await innerAgent
            .RunAsync(inputMessages, session, options, cancellationToken)
            .ConfigureAwait(false);
        if (session == null)
        {
            return response;
        }

        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        RegisterFunctionCalls(
            inputMessages.SelectMany(static message => message.Contents),
            callNames);
        var snapshottedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var rewrittenMessages = new List<ChatMessage>();
        foreach (var message in response.Messages)
        {
            RegisterFunctionCalls(message.Contents, callNames);
            rewrittenMessages.Add(message);
            rewrittenMessages.AddRange(await CreateSnapshotsAsync(
                message.Contents,
                session,
                callNames,
                snapshottedCallIds,
                cancellationToken));
        }

        if (rewrittenMessages.Count != response.Messages.Count)
        {
            response.Messages.Clear();
            foreach (var message in rewrittenMessages)
            {
                response.Messages.Add(message);
            }
        }

        return response;
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var inputMessages = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        RegisterFunctionCalls(
            inputMessages.SelectMany(static message => message.Contents),
            callNames);
        var snapshottedCallIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var update in innerAgent
                           .RunStreamingAsync(inputMessages, session, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            RegisterFunctionCalls(update.Contents, callNames);
            yield return update;

            if (session == null)
            {
                continue;
            }

            var snapshots = await CreateSnapshotsAsync(
                update.Contents,
                session,
                callNames,
                snapshottedCallIds,
                cancellationToken);
            foreach (var snapshot in snapshots)
            {
                yield return ToolStateSnapshots.ToUpdate(snapshot);
            }
        }
    }

    private async ValueTask<IReadOnlyList<ChatMessage>> CreateSnapshotsAsync(
        IEnumerable<AIContent> contents,
        AgentSession session,
        IReadOnlyDictionary<string, string> callNames,
        ISet<string> snapshottedCallIds,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<ChatMessage>();
        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            if (result.Exception != null ||
                !callNames.TryGetValue(result.CallId, out var toolName) ||
                !ModeToolNames.Contains(toolName) ||
                !snapshottedCallIds.Add(result.CallId))
            {
                continue;
            }

            snapshots.Add(await ToolStateSnapshots.CreateModeAsync(
                _provider,
                session,
                toolName,
                result.CallId,
                cancellationToken));
        }

        return snapshots;
    }

    private static void RegisterFunctionCalls(
        IEnumerable<AIContent> contents,
        IDictionary<string, string> callNames)
    {
        foreach (var content in contents)
        {
            var call = content switch
            {
                FunctionCallContent functionCall => functionCall,
                ToolApprovalRequestContent { ToolCall: FunctionCallContent functionCall } =>
                    functionCall,
                ToolApprovalResponseContent { ToolCall: FunctionCallContent functionCall } =>
                    functionCall,
                AlwaysApproveToolApprovalResponseContent
                {
                    InnerResponse.ToolCall: FunctionCallContent functionCall
                } => functionCall,
                _ => null
            };

            if (call is not null &&
                !call.InformationalOnly &&
                !string.IsNullOrWhiteSpace(call.CallId) &&
                !string.IsNullOrWhiteSpace(call.Name))
            {
                callNames[call.CallId] = call.Name;
            }
        }
    }
}
