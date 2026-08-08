using Agw.Agents.Execution.Agentflows;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Utils;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

internal static class ToolApprovalSupport
{
    public static HumanGateApprovalRequest CreateRequest(
        ToolApprovalRequestContent request,
        string nodeId,
        string? nodeName = null)
    {
        var toolName = GetToolName(request);
        var arguments = request.ToolCall is FunctionCallContent functionCall
            ? JsonUtil.Serialize(functionCall.Arguments)
            : null;
        var message = new ChatMessage(ChatRole.Assistant, [request]);
        return new HumanGateApprovalRequest(
            request.RequestId,
            nodeId,
            nodeName,
            "tool-approval",
            $"Allow tool '{toolName}' to run?",
            [message],
            request);
    }

    public static AIContent CreateResponse(
        ToolApprovalRequestContent request,
        HumanGateApprovalDecision decision)
    {
        if (!decision.Approved)
        {
            return request.CreateResponse(approved: false);
        }

        return decision.ApprovalScope?.Trim().ToLowerInvariant() switch
        {
            "always-tool" => request.CreateAlwaysApproveToolResponse(),
            "always-arguments" => request.CreateAlwaysApproveToolWithArgumentsResponse(),
            _ => request.CreateResponse(approved: true)
        };
    }

    public static AgwMessage CreateMessage(HumanGateApprovalRequest request)
    {
        var toolRequest = request.ToolApprovalRequest!;
        var properties = new AdditionalPropertiesDictionary
        {
            { "type", "tool-approval-request" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
            { "mode", request.Mode },
            { "prompt", request.Prompt },
            { "toolName", GetToolName(toolRequest) }
        };

        if (!string.IsNullOrWhiteSpace(request.NodeName))
        {
            properties["nodeName"] = request.NodeName;
        }

        if (toolRequest.ToolCall is FunctionCallContent functionCall &&
            functionCall.Arguments != null)
        {
            properties["arguments"] = JsonUtil.Serialize(functionCall.Arguments);
        }

        return new AgwMessage(
            Guid.CreateVersion7().ToString("N"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = request.Prompt }],
            properties);
    }

    private static string GetToolName(ToolApprovalRequestContent request) =>
        request.ToolCall switch
        {
            FunctionCallContent functionCall => functionCall.Name,
            _ => request.ToolCall?.CallId ?? "unknown"
        };
}
