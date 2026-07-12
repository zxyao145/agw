using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Execution.Agentflows;

public sealed record HumanGateApprovalRequest(
    string RequestId,
    string NodeId,
    string? NodeName,
    string Mode,
    string Prompt,
    IReadOnlyList<ChatMessage> Messages);

public sealed record HumanGateApprovalDecision(
    string RequestId,
    bool Approved,
    string? ResponseText);

public interface IHumanGateApprovalHandler
{
    ValueTask<HumanGateApprovalDecision> WaitForApprovalAsync(
        HumanGateApprovalRequest request,
        CancellationToken cancellationToken);
}
