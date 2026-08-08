using System.Text.Json;

using Microsoft.Extensions.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

public sealed record HumanGateApprovalRequest(
    string RequestId,
    string NodeId,
    string? NodeName,
    string Mode,
    string Prompt,
    IReadOnlyList<ChatMessage> Messages,
    ToolApprovalRequestContent? ToolApprovalRequest = null);

public sealed record HumanGateApprovalDecision(
    string RequestId,
    bool Approved,
    string? ResponseText,
    string ApprovalScope = "once",
    JsonElement? ResponseData = null);

public interface IHumanGateApprovalHandler
{
    bool RequiresHumanResponse(HumanGateApprovalRequest request) => true;

    ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
        HumanGateApprovalRequest request,
        CancellationToken cancellationToken);
}
