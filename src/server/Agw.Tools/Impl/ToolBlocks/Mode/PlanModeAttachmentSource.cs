using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ToolBlocks.Mode;

internal sealed class PlanModeAttachmentSource : IContextAttachmentSource
{
    private const int AssistantMessagesBetweenAttachments = 5;
    private const int FullReminderInterval = 5;
    private const int LastFullReminderOffset = 10;
    private const string FullReminder = $$"""
        Plan mode is active.
        {{ModeToolBlock.PlanModeInstructions}}
        Plan mode is enforced by the server.
        {{
            "Tools that run shell commands or change files, project memory, background tasks, Connections, "
            + "or MCP resources return PlanModeToolNotAllowed while Plan mode is active."
        }}
        """;
    private const string SparseReminder =
        "Plan mode is still active, so follow the Plan mode instructions given earlier in this conversation.";
    private const string ExitReminder = "Plan mode has ended.\nThe conversation is in Execute mode.";

    private readonly AgentModeProvider _provider;

    public PlanModeAttachmentSource(AgentModeProvider provider)
    {
        _provider = provider;
    }

    public async ValueTask<ContextAttachment?> EvaluateAsync(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken
    )
    {
        var mode = await _provider.GetModeAsync(session, cancellationToken).ConfigureAwait(false);
        var planReminders = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var attachmentType = ContextAttachmentHistory.GetAttachmentType(messages[index]);
            if (attachmentType == ContextAttachmentTypes.PlanModeExit)
                break;
            if (attachmentType == ContextAttachmentTypes.PlanMode)
                planReminders++;
        }

        return mode switch
        {
            ModeToolBlock.PlanModeName => CreatePlanReminder(messages, planReminders),
            ModeToolBlock.ExecuteModeName when planReminders > 0 => new(
                ContextAttachmentTypes.PlanModeExit,
                ExitReminder
            ),
            _ => null,
        };
    }

    private static ContextAttachment? CreatePlanReminder(IReadOnlyList<ChatMessage> messages, int planReminders)
    {
        var hasRecentReminder =
            planReminders > 0
            && ContextAttachmentHistory.CountAssistantMessagesAfterLast(
                messages,
                static message => ContextAttachmentHistory.GetAttachmentType(message) == ContextAttachmentTypes.PlanMode
            ) < AssistantMessagesBetweenAttachments;
        if (hasRecentReminder)
            return null;

        var sendFullReminder = planReminders <= LastFullReminderOffset && planReminders % FullReminderInterval == 0;
        return new ContextAttachment(ContextAttachmentTypes.PlanMode, sendFullReminder ? FullReminder : SparseReminder);
    }
}
