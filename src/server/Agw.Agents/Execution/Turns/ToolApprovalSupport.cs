using System.Text.Json;

using Agw.Agents.Execution.Agentflows;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Utils;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// 在 MAF Tool approval 内容与 Agw HumanGate 协议之间转换。
/// </summary>
internal static class ToolApprovalSupport
{
    private const string WorkflowApprovalScopeProperty =
        "Agw.ToolApproval.WorkflowApprovalScope";

    /// <summary>
    /// 从 MAF Tool approval 创建统一 HumanGate 请求。
    /// </summary>
    public static HumanGateApprovalRequest CreateRequest(
        ToolApprovalRequestContent request,
        string nodeId,
        string? nodeName = null)
    {
        var toolName = GetToolName(request);
        var message = new ChatMessage(ChatRole.Assistant, [request]);
        var isInteraction = TryCreateInteractionPayload(request, out _);
        return new HumanGateApprovalRequest(
            request.RequestId,
            nodeId,
            nodeName,
            isInteraction ? "interaction" : "tool-approval",
            isInteraction
                ? "The agent needs your input to continue."
                : $"Allow tool '{toolName}' to run?",
            [message],
            request);
    }

    /// <summary>
    /// 根据人工决策创建一次性或持久范围的 MAF approval 响应。
    /// </summary>
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

    /// <summary>
    /// 创建符合 Agentflow 输入端口类型约束的 approval 响应，并保留持久授权范围。
    /// </summary>
    public static ToolApprovalResponseContent CreateWorkflowResponse(
        ToolApprovalRequestContent request,
        HumanGateApprovalDecision decision)
    {
        var response = CreateResponse(request, decision);
        if (response is ToolApprovalResponseContent singleApproval)
        {
            return singleApproval;
        }

        var alwaysApproval = (AlwaysApproveToolApprovalResponseContent)response;
        alwaysApproval.InnerResponse.AdditionalProperties ??= [];
        alwaysApproval.InnerResponse.AdditionalProperties[WorkflowApprovalScopeProperty] =
            alwaysApproval.AlwaysApproveTool ? "always-tool" : "always-arguments";
        return alwaysApproval.InnerResponse;
    }

    /// <summary>
    /// 在节点 Agent 边界恢复 Agentflow 端口传输时展开的持久授权响应。
    /// </summary>
    public static List<ChatMessage> RestoreWorkflowResponses(
        IReadOnlyList<ChatMessage> messages)
    {
        return messages
            .Select(message =>
            {
                var restoredAny = false;
                var contents = message.Contents
                    .Select(content =>
                    {
                        var restored = RestoreWorkflowResponse(content);
                        restoredAny |= !ReferenceEquals(restored, content);
                        return restored;
                    })
                    .ToList();
                if (!restoredAny)
                {
                    return message;
                }

                var restoredMessage = message.Clone();
                restoredMessage.Contents = contents;
                return restoredMessage;
            })
            .ToList();
    }

    /// <summary>
    /// 创建现有客户端能够渲染的 Tool approval 控制消息。
    /// </summary>
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

    /// <summary>
    /// 从 approval 内容解析原始 Tool 名称。
    /// </summary>
    internal static string GetToolName(ToolApprovalRequestContent request) =>
        request.ToolCall switch
        {
            FunctionCallContent functionCall => functionCall.Name,
            _ => request.ToolCall?.CallId ?? "unknown"
        };

    /// <summary>
    /// 将 Tool 参数复制为可安全跨 durable segment 边界传递的 JSON。
    /// </summary>
    internal static JsonElement? GetArguments(ToolApprovalRequestContent request)
    {
        if (request.ToolCall is not FunctionCallContent functionCall || functionCall.Arguments == null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(JsonUtil.Serialize(functionCall.Arguments));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// 识别 ask_user_question，并只提取 questions 与 metadata，排除模型提供的 answers。
    /// </summary>
    internal static bool TryCreateInteractionPayload(
        ToolApprovalRequestContent request,
        out JsonElement payload)
    {
        payload = default;
        if (!string.Equals(GetToolName(request), "ask_user_question", StringComparison.Ordinal)
            || GetArguments(request) is not { } arguments
            || arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("questions", out var questions))
        {
            return false;
        }

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["questions"] = questions.Clone()
        };
        if (arguments.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            values["metadata"] = metadata.Clone();
        }

        payload = JsonSerializer.SerializeToElement(values, JsonOptions);
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static AIContent RestoreWorkflowResponse(AIContent content)
    {
        if (content is not ToolApprovalResponseContent response ||
            response.AdditionalProperties?.TryGetValue(
                WorkflowApprovalScopeProperty,
                out var scopeValue) != true)
        {
            return content;
        }

        var request = new ToolApprovalRequestContent(response.RequestId, response.ToolCall);
        return scopeValue?.ToString() switch
        {
            "always-tool" => request.CreateAlwaysApproveToolResponse(response.Reason),
            "always-arguments" =>
                request.CreateAlwaysApproveToolWithArgumentsResponse(response.Reason),
            _ => content
        };
    }
}
