using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// Defines and parses the internal turn lifecycle control-message protocol.
/// </summary>
public static class TurnMessageProtocol
{
    public const string StartedType = "turn-start";
    public const string FinishedType = "turn-finished";

    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";
    public const string InterruptedStatus = "interrupted";

    public static string? GetMessageType(AgwMessage message) =>
        message.AdditionalProperties?.TryGetValue("type", out var value) == true ? value as string : null;

    public static bool IsFinished(AgwMessage message) =>
        string.Equals(GetMessageType(message), FinishedType, StringComparison.Ordinal);

    public static bool TryGetFinishedStatus(AgwMessage message, out string? status)
    {
        status = null;
        if (!IsFinished(message))
        {
            return false;
        }

        status = message.AdditionalProperties?.TryGetValue("status", out var value) == true ? value as string : null;
        return true;
    }
}
