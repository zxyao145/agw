namespace Agw.Agents.Contracts.Messages;

public static class ToolMessageTypes
{
    public const string TodoSnapshot = "tool-todo-snapshot";
    public const string ModeStatus = "tool-mode-status";
    public const string BackgroundTaskStatus = "tool-background-task-status";
    public const string Warning = "tool-warning";

    public static bool IsToolMessage(string? type) =>
        type is TodoSnapshot or ModeStatus or BackgroundTaskStatus or Warning;
}
