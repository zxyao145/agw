using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.Tools;

internal static class ToolStateSnapshots
{
    private static readonly IReadOnlySet<string> BackgroundAgentToolNames = new HashSet<string>(
        [
            "background_agents_start_task",
            "background_agents_wait_for_first_completion",
            "background_agents_get_task_results",
            "background_agents_get_all_tasks",
            "background_agents_continue_task",
            "background_agents_clear_completed_task",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    public static ValueTask<IReadOnlyList<ChatMessage>> CreateAsync(
        AIAgent agent,
        AgentSession session,
        IEnumerable<ChatMessage> turnMessages,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(turnMessages);

        var usedToolNames = GetCompletedToolNames(turnMessages);
        var messages = new List<ChatMessage>();

        var backgroundProvider = agent.GetService<BackgroundAgentsProvider>();
        if (backgroundProvider != null && HasToolName(usedToolNames, BackgroundAgentToolNames))
        {
            var tasks = backgroundProvider.GetIncompleteTasks(session);
            messages.Add(
                CreateMessage(
                    ToolMessageTypes.BackgroundTaskStatus,
                    new AdditionalPropertiesDictionary
                    {
                        ["tasks"] = tasks
                            .Select(static task => new
                            {
                                id = task.Id,
                                agentName = task.AgentName,
                                description = task.Description,
                                status = task.Status.ToString().ToLowerInvariant(),
                            })
                            .ToArray(),
                    }
                )
            );
        }

        return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(messages);
    }

    public static async ValueTask<ChatMessage> CreateModeAsync(
        AgentModeProvider provider,
        AgentSession session,
        string toolName,
        string callId,
        CancellationToken cancellationToken
    )
    {
        var mode = await provider.GetModeAsync(session, cancellationToken).ConfigureAwait(false);
        return CreateMessage(
            ToolMessageTypes.ModeStatus,
            new AdditionalPropertiesDictionary
            {
                ["toolName"] = toolName,
                ["callId"] = callId,
                ["mode"] = mode,
            }
        );
    }

    public static async ValueTask<ChatMessage> CreateTodoAsync(
        TodoProvider provider,
        AgentSession session,
        string toolName,
        string callId,
        CancellationToken cancellationToken
    )
    {
        var items = await provider.GetAllTodosAsync(session, cancellationToken).ConfigureAwait(false);
        return CreateMessage(
            ToolMessageTypes.TodoSnapshot,
            new AdditionalPropertiesDictionary
            {
                ["toolName"] = toolName,
                ["callId"] = callId,
                ["items"] = items
                    .Select(static item => new
                    {
                        id = item.Id,
                        title = item.Title,
                        description = item.Description,
                        isComplete = item.IsComplete,
                    })
                    .ToArray(),
            }
        );
    }

    private static HashSet<string> GetCompletedToolNames(IEnumerable<ChatMessage> turnMessages)
    {
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var content in turnMessages.SelectMany(static message => message.Contents))
        {
            var call = content switch
            {
                FunctionCallContent functionCall => functionCall,
                ToolApprovalRequestContent { ToolCall: FunctionCallContent functionCall } => functionCall,
                _ => null,
            };

            if (
                call is not null
                && !string.IsNullOrWhiteSpace(call.CallId)
                && !call.InformationalOnly
                && !string.IsNullOrWhiteSpace(call.Name)
            )
            {
                callNames[call.CallId] = call.Name;
            }
        }

        var completedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var result in turnMessages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>()
        )
        {
            if (callNames.TryGetValue(result.CallId, out var name) && !string.IsNullOrWhiteSpace(name))
            {
                completedToolNames.Add(name);
            }
        }

        return completedToolNames;
    }

    private static bool HasToolName(IReadOnlySet<string> usedToolNames, IReadOnlySet<string> expectedToolNames) =>
        usedToolNames.Any(expectedToolNames.Contains);

    public static AgentResponseUpdate ToUpdate(ChatMessage message) =>
        new(message.Role, message.Contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties,
        };

    public static ChatMessage ToMessage(AgentResponseUpdate update) =>
        new(update.Role ?? ChatRole.System, update.Contents)
        {
            AuthorName = update.AuthorName,
            MessageId = update.MessageId,
            AdditionalProperties = update.AdditionalProperties,
        };

    public static bool IsToolMessage(ChatMessage message) => IsToolMessage(message.AdditionalProperties);

    public static bool IsToolMessage(AgentResponseUpdate update) => IsToolMessage(update.AdditionalProperties);

    public static bool RequiresSeparatePersistence(ChatMessage message) =>
        IsToolMessage(message) && !IsHistoryPrelude(message.AdditionalProperties);

    public static bool RequiresSeparatePersistence(AgentResponseUpdate update) =>
        IsToolMessage(update) && !IsHistoryPrelude(update.AdditionalProperties);

    private static bool IsToolMessage(AdditionalPropertiesDictionary? properties) =>
        properties?.TryGetValue("type", out var type) == true && ToolMessageTypes.IsToolMessage(type?.ToString());

    private static bool IsHistoryPrelude(AdditionalPropertiesDictionary? properties)
    {
        if (
            properties?.TryGetValue("type", out var type) != true
            || !string.Equals(type?.ToString(), ToolMessageTypes.Warning, StringComparison.Ordinal)
        )
        {
            return false;
        }

        return properties.TryGetValue("persistSeparately", out var persistSeparately) != true
            || persistSeparately is not true;
    }

    private static ChatMessage CreateMessage(string type, AdditionalPropertiesDictionary properties)
    {
        properties["type"] = type;
        return new ChatMessage(ChatRole.System, [new TextContent(string.Empty)])
        {
            MessageId = Guid.CreateVersion7().ToString("N"),
            AuthorName = "tools",
            AdditionalProperties = properties,
        };
    }
}
