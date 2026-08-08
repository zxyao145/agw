using System.Runtime.CompilerServices;

using Agw.Agents.Execution.Agents.Tools;
using Agw.Shared.Contracts.Agents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class ToolInvocationWarningMiddleware
{
    private readonly IReadOnlyDictionary<string, string> _warnings;

    public ToolInvocationWarningMiddleware(
        IReadOnlyDictionary<string, string> warnings)
    {
        _warnings = warnings;
    }

    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        var response = await innerAgent
            .RunAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false);
        var rewrittenMessages = AddInvocationWarnings(response.Messages);
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
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnedCallIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var update in innerAgent
                           .RunStreamingAsync(messages, session, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            RegisterFunctionCalls(update.Contents, callNames);
            foreach (var warning in CreateWarnings(
                         update.Contents,
                         callNames,
                         warnedCallIds))
            {
                yield return ToolStateSnapshots.ToUpdate(warning);
            }

            yield return update;
        }
    }

    private List<ChatMessage> AddInvocationWarnings(
        IEnumerable<ChatMessage> messages)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChatMessage>();
        foreach (var message in messages)
        {
            RegisterFunctionCalls(message.Contents, callNames);
            result.AddRange(CreateWarnings(
                message.Contents,
                callNames,
                warnedCallIds));
            result.Add(message);
        }

        return result;
    }

    private static void RegisterFunctionCalls(
        IEnumerable<AIContent> contents,
        IDictionary<string, string> callNames)
    {
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            if (!string.IsNullOrWhiteSpace(call.CallId) &&
                !string.IsNullOrWhiteSpace(call.Name))
            {
                callNames[call.CallId] = call.Name;
            }
        }
    }

    private IEnumerable<ChatMessage> CreateWarnings(
        IEnumerable<AIContent> contents,
        IReadOnlyDictionary<string, string> callNames,
        ISet<string> warnedCallIds)
    {
        foreach (var result in contents.OfType<FunctionResultContent>())
        {
            if (!warnedCallIds.Add(result.CallId) ||
                !callNames.TryGetValue(result.CallId, out var toolName) ||
                !_warnings.TryGetValue(toolName, out var warning))
            {
                continue;
            }

            yield return new ChatMessage(
                ChatRole.System,
                [new TextContent(warning)])
            {
                MessageId = Guid.CreateVersion7().ToString("N"),
                AuthorName = "tools",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["type"] = ToolMessageTypes.Warning,
                    ["toolName"] = toolName,
                    ["callId"] = result.CallId,
                    ["persistSeparately"] = true
                }
            };
        }
    }
}
