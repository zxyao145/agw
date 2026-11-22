using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

internal sealed class TodoReminderAttachmentSource : IContextAttachmentSource
{
    private const int AssistantMessagesSinceTodoCall = 10;
    private const int AssistantMessagesBetweenReminders = 10;
    private const string Reminder =
        "The todo tools have not been used recently.\n"
        + "Use them when the current work benefits from tracking, and ignore this reminder when it does not apply.";

    private readonly AgwTodoProvider _provider;
    private readonly HashSet<string> _toolNames;

    public TodoReminderAttachmentSource(AgwTodoProvider provider, IReadOnlyList<string> toolNames)
    {
        _provider = provider;
        _toolNames = toolNames.ToHashSet(StringComparer.Ordinal);
    }

    public async ValueTask<ContextAttachment?> EvaluateAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        var sinceCall = ContextAttachmentHistory.CountAssistantMessagesAfterLast(
            messages,
            message => message.Contents.OfType<FunctionCallContent>().Any(call => _toolNames.Contains(call.Name))
        );
        if (sinceCall < AssistantMessagesSinceTodoCall)
            return null;

        var sinceReminder = ContextAttachmentHistory.CountAssistantMessagesAfterLast(
            messages,
            static message => ContextAttachmentHistory.GetAttachmentType(message) == ContextAttachmentTypes.TodoReminder
        );
        if (sinceReminder < AssistantMessagesBetweenReminders)
            return null;

        var items = await _provider.GetAllTodosAsync(session, cancellationToken).ConfigureAwait(false);
        return new ContextAttachment(
            ContextAttachmentTypes.TodoReminder,
            $"{Reminder}\n\n{AgwTodoProvider.FormatTodoListMessage(items)}"
        );
    }
}
