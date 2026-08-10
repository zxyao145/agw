using System.Text.Json;

using Agw.Agents.Execution.Agentflows;
using Agw.Agents.Execution.Turns;
using Agw.Shared.AgwMsgVm;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 在运行时审批对象、PostgreSQL pending 快照和客户端控制消息之间转换。
/// </summary>
internal static class DurableHumanInteractionMapper
{
    /// <summary>
    /// 从运行时 HumanGate 请求创建可写入 PostgreSQL 状态记录的最小快照。
    /// </summary>
    public static DurableHumanInteractionSnapshot FromRequest(HumanGateApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ToolApprovalRequest is { } toolApproval)
        {
            var functionCall = toolApproval.ToolCall as FunctionCallContent;
            var isInteraction = ToolApprovalSupport.TryCreateInteractionPayload(
                toolApproval,
                out var interactionPayload);
            var arguments = ToolApprovalSupport.GetArguments(toolApproval);
            // ask_user_question 只持久化 questions/metadata；answers 必须来自后续 HumanResponseCommand。
            return new DurableHumanInteractionSnapshot
            {
                RequestId = request.RequestId,
                Kind = isInteraction ? "questions" : "tool-approval",
                NodeId = request.NodeId,
                NodeName = request.NodeName,
                ToolName = ToolApprovalSupport.GetToolName(toolApproval),
                CallId = functionCall?.CallId,
                Prompt = request.Prompt,
                Payload = isInteraction ? interactionPayload : null,
                Arguments = isInteraction ? null : arguments
            };
        }

        var inputPreview = request.Messages.LastOrDefault()?.Text;
        JsonElement? payload = string.IsNullOrWhiteSpace(inputPreview)
            ? null
            : JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["inputPreview"] = inputPreview });
        return new DurableHumanInteractionSnapshot
        {
            RequestId = request.RequestId,
            Kind = request.Mode,
            NodeId = request.NodeId,
            NodeName = request.NodeName,
            Prompt = request.Prompt,
            Payload = payload
        };
    }

    /// <summary>
    /// 从 durable pending 快照重建现有客户端能够渲染的人机交互控制消息。
    /// </summary>
    public static AgwMessage ToMessage(
        DurableHumanInteractionSnapshot interaction,
        Guid? executionId = null)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        var properties = new AdditionalPropertiesDictionary
        {
            ["requestId"] = interaction.RequestId,
            ["prompt"] = interaction.Prompt
        };
        if (executionId.HasValue)
        {
            properties["executionId"] = executionId.Value.ToString("D");
        }
        if (!string.IsNullOrWhiteSpace(interaction.NodeId))
        {
            properties["nodeId"] = interaction.NodeId;
        }
        if (!string.IsNullOrWhiteSpace(interaction.NodeName))
        {
            properties["nodeName"] = interaction.NodeName;
        }
        if (!string.IsNullOrWhiteSpace(interaction.ToolName))
        {
            properties["toolName"] = interaction.ToolName;
        }
        if (!string.IsNullOrWhiteSpace(interaction.CallId))
        {
            properties["callId"] = interaction.CallId;
        }

        if (string.Equals(interaction.Kind, "questions", StringComparison.Ordinal))
        {
            properties["type"] = "human-interaction-request";
            properties["interactionKind"] = interaction.Kind;
            if (interaction.Payload.HasValue)
            {
                properties["payload"] = interaction.Payload.Value;
            }
        }
        else if (!string.IsNullOrWhiteSpace(interaction.ToolName))
        {
            properties["type"] = "tool-approval-request";
            properties["mode"] = interaction.Kind;
            if (interaction.Arguments.HasValue)
            {
                properties["arguments"] = interaction.Arguments.Value.GetRawText();
            }
        }
        else
        {
            properties["type"] = "human-gate-request";
            properties["mode"] = interaction.Kind;
            if (interaction.Payload is { ValueKind: JsonValueKind.Object } payload
                && payload.TryGetProperty("inputPreview", out var inputPreview)
                && inputPreview.ValueKind == JsonValueKind.String)
            {
                properties["inputPreview"] = inputPreview.GetString();
            }
        }

        return new AgwMessage(
            Guid.CreateVersion7().ToString("N"),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = interaction.Prompt }],
            properties);
    }
}
