using System.Runtime.CompilerServices;
using Agw.Agents.Execution.Agents.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Middleware;

internal sealed class ToolWarningMiddleware
{
    private readonly IReadOnlyList<string> _warnings;

    public ToolWarningMiddleware(IReadOnlyList<string> warnings)
    {
        _warnings = warnings;
    }

    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        var warningMessages = CreateMessages();
        ConversationHistoryPrelude.Set(session, warningMessages);
        try
        {
            var response = await innerAgent
                .RunAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false);
            for (var index = warningMessages.Count - 1; index >= 0; index--)
            {
                response.Messages.Insert(0, warningMessages[index]);
            }

            return response;
        }
        finally
        {
            ConversationHistoryPrelude.Clear(session);
        }
    }

    public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var warningMessages = CreateMessages();
        ConversationHistoryPrelude.Set(session, warningMessages);
        try
        {
            foreach (var warningMessage in warningMessages)
            {
                yield return ToolStateSnapshots.ToUpdate(warningMessage);
            }

            await foreach (
                var update in innerAgent
                    .RunStreamingAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return update;
            }
        }
        finally
        {
            ConversationHistoryPrelude.Clear(session);
        }
    }

    private List<ChatMessage> CreateMessages() => _warnings.Select(CreateMessage).ToList();

    private static ChatMessage CreateMessage(string warning) =>
        new(ChatRole.System, [new TextContent(warning)])
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
            AuthorName = "tools",
            AdditionalProperties = new AdditionalPropertiesDictionary { ["type"] = ToolMessageTypes.Warning },
        };
}
