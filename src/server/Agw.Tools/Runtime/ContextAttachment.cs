namespace Agw.Tools.Runtime;

public sealed record ContextAttachment(string Type, string Text);

public static class ContextAttachmentTypes
{
    public const string PlanMode = "plan_mode";
    public const string PlanModeExit = "plan_mode_exit";
    public const string TodoReminder = "todo_reminder";
}
