using System.Text.Json;
using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Agw.Agents.Execution.Runtimes.Durable.Contracts;
using Agw.Shared.Utils;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows.Messaging;

/// <summary>
/// 确定性映射 Framework 内容与人工交互协议；新控制消息的 ID 由执行边界提供。
/// </summary>
internal static class AgentflowMessageMapper
{
    private const string DefaultHumanGateMode = "approval";
    private const string DefaultHumanGatePrompt = "Human approval is required to continue.";
    private static readonly JsonSerializerOptions JsonOptions = WebJsonOptions.Default;

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
    internal static InteractionRequest? CreateInteractionRequest(
        ExternalRequest externalRequest,
        IReadOnlyDictionary<string, AgentflowHumanGateNode> humanGateNodes,
        IInteractionRequestRegistry? registry = null
    )
    {
        if (externalRequest.TryGetDataAs(out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest))
        {
            return MafApprovalAdapter.CreateRequest(
                toolApprovalRequest,
                externalRequest.PortInfo.PortId,
                registry: registry
            );
        }

        return humanGateNodes.TryGetValue(externalRequest.PortInfo.PortId, out var humanGateNode)
            ? CreateWorkflowGateInteraction(externalRequest, humanGateNode)
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

    internal static WorkflowGateInteraction CreateWorkflowGateInteraction(
        ExternalRequest externalRequest,
        AgentflowHumanGateNode node
    )
    {
        // 显式 HumanGate 使用 WorkflowGateInteraction 表达工作流是否继续，FullAccess 不会跳过它。
        // 这里只提取节点配置和输入预览；等待、发布及拒绝后的终止交给交互会话与 Runner。
        // 节点和 SDK 请求 ID 共同确定交互身份，使 checkpoint 恢复仍能关联同一次关卡请求。
        var config = ReadHumanGateConfig(node);
        var messages =
            externalRequest.TryGetDataAs<List<ChatMessage>>(out var requestedMessages) && requestedMessages != null
                ? requestedMessages
                : [];

        var mode = string.IsNullOrWhiteSpace(config.HumanMode) ? DefaultHumanGateMode : config.HumanMode.Trim();
        var prompt = string.IsNullOrWhiteSpace(config.HumanPrompt) ? DefaultHumanGatePrompt : config.HumanPrompt.Trim();

        return new WorkflowGateInteraction
        {
            InteractionId = InteractionIdentity.ForProviderRequest(node.NodeId, externalRequest.RequestId),
            Source = new InteractionSource
            {
                NodeId = node.NodeId,
                NodeName = node.Name,
                ProviderRequestId = externalRequest.RequestId,
            },
            Mode = mode,
            Prompt = prompt,
            InputPreview = messages.LastOrDefault()?.Text,
        };
    }

    internal static List<ChatMessage> CreateHumanGateResponseMessages(
        IReadOnlyList<ChatMessage> messages,
        WorkflowGateDecision decision
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

    internal static IReadOnlyList<ChatMessage> GetHumanGateMessages(ExternalRequest request) =>
        request.TryGetDataAs<List<ChatMessage>>(out var messages) && messages is not null ? messages : [];

    internal static AgwMessage CreateHumanGateRejectedMessage(WorkflowGateInteraction request, string messageId)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { "type", "human-gate-rejected" },
            { "requestId", request.InteractionId },
            { "nodeId", request.Source.NodeId },
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
            { "providerRequestId", request.RequestId },
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
