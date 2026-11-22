using Agw.Agents.Execution.Agentflows.Workflows;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agentflows.Messaging;

/// <summary>
/// 确定性映射 Framework 内容与人工交互协议；新控制消息的 ID 由执行边界提供。
/// </summary>
internal static class AgentflowMessageMapper
{
    private const string DefaultHumanGatePrompt = "Human approval is required to continue.";

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
            IEnumerable<AgentResponseUpdate> updates => ConvertChatMessages(updates.ToAgentResponse().Messages),
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
        IReadOnlyDictionary<string, string> nodeIdsByRequestPortId,
        IInteractionRequestRegistry? registry = null
    )
    {
        if (externalRequest.TryGetDataAs(out Microsoft.Extensions.AI.ToolApprovalRequestContent? toolApprovalRequest))
        {
            var interaction = MafApprovalAdapter.CreateRequest(
                toolApprovalRequest,
                externalRequest.PortInfo.PortId,
                registry: registry
            );
            if (
                interaction.Source.ProviderScopeId == null
                && string.Equals(interaction.Source.NodeId, externalRequest.PortInfo.PortId, StringComparison.Ordinal)
                && nodeIdsByRequestPortId.TryGetValue(externalRequest.PortInfo.PortId, out var nodeId)
            )
            {
                return interaction with
                {
                    InteractionId = InteractionIdentity.ForProviderRequest(nodeId, toolApprovalRequest.RequestId),
                    Source = interaction.Source with
                    {
                        NodeId = nodeId,
                        NodePath = interaction.Source.NodePath ?? nodeId,
                        ProviderScopeId = externalRequest.PortInfo.PortId,
                    },
                };
            }

            return interaction;
        }

        return humanGateNodes.TryGetValue(externalRequest.PortInfo.PortId, out var humanGateNode)
            ? CreateWorkflowGateInteraction(externalRequest, humanGateNode)
            : null;
    }

    internal static WorkflowGateInteraction CreateWorkflowGateInteraction(
        ExternalRequest externalRequest,
        AgentflowHumanGateNode node
    )
    {
        // 显式 HumanGate 使用 WorkflowGateInteraction 表达工作流是否继续，FullAccess 不会跳过它。
        // 这里只提取节点配置和输入预览；等待、发布及拒绝后的终止交给交互会话与 Runner。
        // 节点和 SDK 请求 ID 共同确定交互身份，使 checkpoint 恢复仍能关联同一次关卡请求。
        var messages =
            externalRequest.TryGetDataAs<List<ChatMessage>>(out var requestedMessages) && requestedMessages != null
                ? requestedMessages
                : [];

        var prompt = string.IsNullOrWhiteSpace(node.Options.HumanPrompt)
            ? DefaultHumanGatePrompt
            : node.Options.HumanPrompt.Trim();

        return new WorkflowGateInteraction
        {
            InteractionId = InteractionIdentity.ForProviderRequest(node.NodeId, externalRequest.RequestId),
            Source = new InteractionSource
            {
                NodeId = node.NodeId,
                NodePath = node.NodePath ?? node.NodeId,
                NodeName = node.Name,
                ProviderRequestId = externalRequest.RequestId,
            },
            Prompt = prompt,
            Choices = node
                .Options.Choices.Select(choice => new WorkflowGateChoice(choice.ChoiceId, choice.Label))
                .ToArray(),
            AllowResponseText = node.Options.AllowResponseText,
            InputPreview = messages.LastOrDefault()?.Text,
        };
    }

    internal static List<ChatMessage> CreateHumanGateResponseMessages(
        IReadOnlyList<ChatMessage> messages,
        WorkflowGateInteraction request,
        WorkflowGateDecision decision
    )
    {
        var responseMessages = messages.ToList();
        var choice = request.Choices.Single(choice =>
            string.Equals(choice.ChoiceId, decision.ChoiceId, StringComparison.Ordinal)
        );
        var text = decision.ResponseText?.Trim();
        if (string.IsNullOrEmpty(text))
            text = choice.Label;
        responseMessages.Add(
            new ChatMessage(ChatRole.User, text)
            {
                AuthorName = AgwMessageClassifier.HumanAuthorName,
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [AgwMessageClassifier.ChoiceIdKey] = choice.ChoiceId,
                },
            }
        );

        return responseMessages;
    }

    internal static AgwMessage CreateNodeStatusMessage(
        string nodeId,
        string status,
        string? interactionId,
        string messageId
    )
    {
        var properties = new AdditionalPropertiesDictionary
        {
            [AgwMessageClassifier.TypeKey] = AgwMessageTypes.AgentflowNodeStatus,
            [AgwMessageClassifier.NodeIdKey] = nodeId,
            [AgwMessageClassifier.StatusKey] = status,
        };
        if (interactionId is not null)
            properties[AgwMessageClassifier.InteractionIdKey] = interactionId;
        return new AgwMessage(messageId, Constants.DefaultAgentAuthor, AiRole.System, [], properties);
    }

    internal static IReadOnlyList<ChatMessage> GetHumanGateMessages(ExternalRequest request) =>
        request.TryGetDataAs<List<ChatMessage>>(out var messages) && messages is not null ? messages : [];

    internal static AgwMessage CreateHumanGateRejectedMessage(WorkflowGateInteraction request, string messageId)
    {
        var additionalProperties = new AdditionalPropertiesDictionary
        {
            { AgwMessageClassifier.TypeKey, AgwMessageTypes.HumanGateRejected },
            { "requestId", request.InteractionId },
            { AgwMessageClassifier.NodeIdKey, request.Source.NodeId },
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
            { AgwMessageClassifier.TypeKey, AgwMessageTypes.HumanGateUnavailable },
            { AgwMessageClassifier.NodeIdKey, node.NodeId },
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
            { "type", AgwMessageTypes.ToolApprovalUnavailable },
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
        var additionalProperties = new AdditionalPropertiesDictionary { { "type", AgwMessageTypes.WorkflowError } };

        return new AgwMessage(
            messageId,
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwErrorContent { Content = GetWorkflowErrorText(exception) }],
            additionalProperties
        );
    }

    internal static string GetWorkflowErrorText(Exception? exception)
    {
        while (exception is System.Reflection.TargetInvocationException { InnerException: { } inner })
            exception = inner;
        return exception?.Message ?? "Workflow execution failed.";
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

    internal static IEnumerable<AgwMessage> MapEvent(
        WorkflowEvent evt,
        ISet<(string ExecutorId, string MessageId, AiRole Role, string? Author)> deliveredMessages
    )
    {
        var executorId = evt switch
        {
            AgentResponseUpdateEvent update => update.ExecutorId,
            AgentResponseEvent response => response.ExecutorId,
            WorkflowOutputEvent output => output.ExecutorId,
            _ => string.Empty,
        };
        foreach (var message in MapEvent(evt))
        {
            var isResult = AgwMessageClassifier.IsResult(message);
            var firstDelivery =
                string.IsNullOrWhiteSpace(message.MessageId)
                || message.Contents.Count == 0
                || deliveredMessages.Add(
                    (isResult ? string.Empty : executorId, message.MessageId, message.Role, message.Author)
                );
            if (evt is AgentResponseUpdateEvent || firstDelivery)
                yield return message;
        }
    }
}
