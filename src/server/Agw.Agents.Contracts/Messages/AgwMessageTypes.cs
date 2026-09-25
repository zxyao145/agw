namespace Agw.Agents.Contracts.Messages;

/// <summary>
/// 消息 additionalProperties 中 type 的全部取值。
/// Every value of the type key in message additionalProperties.
/// </summary>
public static class AgwMessageTypes
{
    public const string TurnStart = "agw-turn-start";
    public const string TurnFinished = "agw-turn-finished";
    public const string StepDiscarded = "agw-step-discarded";
    public const string InteractionRequest = "interaction-request";
    public const string AgentflowCheckpoint = "agentflow-checkpoint";
    public const string HumanGateRejected = "human-gate-rejected";
    public const string HumanGateUnavailable = "human-gate-unavailable";
    public const string ToolApprovalUnavailable = "tool-approval-unavailable";
    public const string WorkflowError = "workflow-error";
    public const string PermissionStatus = "permission-status";
    public const string ModeStatus = "mode-status";
    public const string ModeChangeFailed = "mode-change-failed";
    public const string Result = "result";
    public const string ToolTodoSnapshot = "tool-todo-snapshot";
    public const string ToolModeStatus = "tool-mode-status";
    public const string ToolBackgroundTaskStatus = "tool-background-task-status";
    public const string ToolWarning = "tool-warning";

    public const string HumanGatePrefix = "human-gate-";
    public const string ToolApprovalPrefix = "tool-approval-";
}

/// <summary>
/// agw-turn-finished 的 status 取值。
/// The status values of agw-turn-finished.
/// </summary>
public static class AgwTurnStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
}
