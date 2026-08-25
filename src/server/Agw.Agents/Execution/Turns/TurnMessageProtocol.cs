namespace Agw.Agents.Execution.Turns;

/// <summary>
/// Defines and parses the internal turn lifecycle control-message protocol.
/// </summary>
public static class TurnMessageProtocol
{
    public const string StartedType = "turn-start";
    public const string FinishedType = AgentExecutionMessageProtocol.FinishedType;

    public const string CompletedStatus = AgentExecutionMessageProtocol.CompletedStatus;
    public const string FailedStatus = AgentExecutionMessageProtocol.FailedStatus;
    public const string InterruptedStatus = AgentExecutionMessageProtocol.InterruptedStatus;

    public static string? GetMessageType(AgwMessage message) => AgentExecutionMessageProtocol.GetMessageType(message);

    public static bool IsFinished(AgwMessage message) => AgentExecutionMessageProtocol.IsFinished(message);

    public static bool TryGetFinishedStatus(AgwMessage message, out string? status) =>
        AgentExecutionMessageProtocol.TryGetFinishedStatus(message, out status);
}
