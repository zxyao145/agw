using System.Text.Json;
using Agw.Agents.Execution.Durable;
using Agw.Agents.Execution.Turns;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows;

/// <summary>
/// 确定性映射 Framework 内容与人工交互协议；新控制消息的 ID 由执行边界提供。
/// </summary>
internal static class AgentflowMessageMapper
{
    private const string DefaultHumanGateMode = "approval";
    private const string DefaultHumanGatePrompt = "Human approval is required to continue.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static IReadOnlyList<AgwMessage> CreateWorkflowOutputMessages(object? data)
    {
        return data switch
        {
            null => [],
            ChatMessage message => ConvertChatMessages([message]),
            IEnumerable<ChatMessage> messages => ConvertChatMessages(messages),
            AgentResponse response => ConvertChatMessages(response.Messages),
            IEnumerable<AgentResponse> responses => responses
                .SelectMany(response => ConvertChatMessages(response.Messages))
                .ToList(),
            AgentResponseUpdate update => update.ToAiMessage() is { } message ? [message] : [],
            IEnumerable<AgentResponseUpdate> updates => updates
                .Select(update => update.ToAiMessage())
                .OfType<AgwMessage>()
                .ToList(),
            _ => [],
        };
    }

    private static IReadOnlyList<AgwMessage> ConvertChatMessages(IEnumerable<ChatMessage> messages)
    {
        return messages.Select(message => message.ToAiMessage()).OfType<AgwMessage>().ToList();
    }

    /// <summary>
    /// 将 Agentflow external request 映射为可持久化的 Tool approval 或 HumanGate 请求。
    /// </summary>
    internal static HumanGateApprovalRequest? CreateDurableApprovalRequest(
        ExternalRequest externalRequest,
        IReadOnlyDictionary<string, AgentflowHumanGateNode> humanGateNodes
    )
    {
        if (externalRequest.TryGetDataAs(out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest))
        {
            return ToolApprovalSupport.CreateRequest(toolApprovalRequest, externalRequest.PortInfo.PortId);
        }

        return humanGateNodes.TryGetValue(externalRequest.PortInfo.PortId, out var humanGateNode)
            ? CreateHumanGateApprovalRequest(externalRequest, humanGateNode)
            : null;
    }

    /// <summary>
    /// 创建与当前 execution 和 segment 对齐的 Agentflow 失败结果。
    /// </summary>
    internal static DurableExecutionSegmentResult CreateDurableFailure(
        DurableExecutionSegmentInput input,
        string error
    ) =>
        new()
        {
            ExecutionId = input.ExecutionId,
            SegmentIndex = input.SegmentIndex,
            Status = DurableExecutionSegmentStatus.Failed,
            ErrorMessage = error,
        };

    internal static HumanGateApprovalRequest CreateHumanGateApprovalRequest(
        ExternalRequest externalRequest,
        AgentflowHumanGateNode node
    )
    {
        var config = ReadHumanGateConfig(node);
        var messages =
            externalRequest.TryGetDataAs<List<ChatMessage>>(out var requestedMessages) && requestedMessages != null
                ? requestedMessages
                : [];

        var mode = string.IsNullOrWhiteSpace(config.HumanMode) ? DefaultHumanGateMode : config.HumanMode.Trim();
        var prompt = string.IsNullOrWhiteSpace(config.HumanPrompt) ? DefaultHumanGatePrompt : config.HumanPrompt.Trim();

        return new HumanGateApprovalRequest(externalRequest.RequestId, node.NodeId, node.Name, mode, prompt, messages);
    }

    internal static List<ChatMessage> CreateHumanGateResponseMessages(
        IReadOnlyList<ChatMessage> messages,
        HumanGateApprovalDecision decision
    )
    {
        var responseMessages = messages.ToList();
        responseMessages.Add(
            new ChatMessage(
                ChatRole.User,
                string.IsNullOrWhiteSpace(decision.ResponseText) ? string.Empty : decision.ResponseText.Trim()
            )
            {
                AuthorName = "human",
            }
        );

        return responseMessages;
    }

    internal static AgwMessage CreateHumanGateApprovalRequestMessage(HumanGateApprovalRequest request, string messageId)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-request" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
            { "mode", request.Mode },
            { "prompt", request.Prompt },
        };

        if (!string.IsNullOrWhiteSpace(request.NodeName))
        {
            additionalProperties["nodeName"] = request.NodeName;
        }

        var latestMessageText = request.Messages.LastOrDefault()?.Text;
        if (!string.IsNullOrWhiteSpace(latestMessageText))
        {
            additionalProperties["inputPreview"] = latestMessageText;
        }

        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = request.Prompt }],
            additionalProperties
        );
    }

    internal static AgwMessage CreateHumanGateRejectedMessage(HumanGateApprovalRequest request, string messageId)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-rejected" },
            { "requestId", request.RequestId },
            { "nodeId", request.NodeId },
        };

        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "HumanGate rejected. Workflow stopped." }],
            additionalProperties
        );
    }

    internal static AgwMessage CreateHumanGateUnavailableMessage(AgentflowHumanGateNode node, string messageId)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-unavailable" },
            { "nodeId", node.NodeId },
        };

        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = "HumanGate requires an active approval channel." }],
            additionalProperties
        );
    }

    internal static AgwMessage CreateToolApprovalUnavailableMessage(
        Microsoft.Extensions.AI.ToolApprovalRequestContent request,
        string messageId
    )
    {
        var properties = new AdditionalPropertiesDictionary
        {
            { "type", "tool-approval-unavailable" },
            { "requestId", request.RequestId },
        };
        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = "Tool approval requires an active interactive approval channel." }],
            properties
        );
    }

    internal static AgwMessage CreateWorkflowErrorMessage(Exception? exception, string messageId)
    {
        var additionalProperties = new AdditionalPropertiesDictionary { { "type", "workflow-error" } };

        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = exception?.Message ?? "Workflow execution failed." }],
            additionalProperties
        );
    }

    private static HumanGateConfig ReadHumanGateConfig(AgentflowHumanGateNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ConfigJson))
        {
            return new HumanGateConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<HumanGateConfig>(node.ConfigJson, JsonOptions) ?? new HumanGateConfig();
        }
        catch (JsonException)
        {
            return new HumanGateConfig();
        }
    }

    private sealed record HumanGateConfig
    {
        public string? HumanMode { get; init; }

        public string? HumanPrompt { get; init; }
    }

    // 内容事件保留 Framework 提供的 ID；错误和交互消息由专用方法映射。
    internal static IEnumerable<AgwMessage> MapEvent(WorkflowEvent evt) =>
        evt switch
        {
            AgentResponseUpdateEvent { Data: AgentResponseUpdate update } => update.ToAiMessage() is { } message
                ? [message]
                : [],
            AgentResponseEvent { Data: AgentResponse response } => response
                .Messages.Select(message => message.ToAiMessage())
                .OfType<AgwMessage>(),
            WorkflowOutputEvent output => CreateWorkflowOutputMessages(output.Data),
            _ => [],
        };
}
