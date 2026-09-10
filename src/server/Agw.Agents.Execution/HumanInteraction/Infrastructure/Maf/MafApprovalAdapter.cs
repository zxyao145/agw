using System.Text.Json;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

/// <summary>
/// Converts MAF approval envelopes to typed execution interactions.
/// </summary>
internal static class MafApprovalAdapter
{
    internal const string GrantScopeProperty = "Agw.ToolApproval.Grant";

    // MAF names an agent's request port after its executor binding, not its business node ID.
    internal static string GetWorkflowRequestScope(AIAgent agent) => $"{agent.BindAsExecutor().Id}_UserInput";

    /// <summary>
    /// The scope is the standalone node or the workflow SDK request port.
    /// </summary>
    public static InteractionRequest CreateRequest(
        ToolApprovalRequestContent request,
        string scopeId,
        string? nodeName = null,
        IInteractionRequestRegistry? registry = null
    )
    {
        var description = HumanInteractionToolMetadata.Read(request);
        var declaredScope = description?.Source.ProviderScopeId ?? description?.Source.NodeId;
        if (declaredScope is not null && declaredScope != scopeId)
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                "The input was delivered through a different SDK request scope."
            );
        if (registry?.Find(request.RequestId, scopeId) is { } saved)
        {
            if (saved.Source.CallId != request.ToolCall?.CallId || saved.Source.ToolName != GetToolName(request))
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    "The tool does not match its saved interaction."
                );
            return description is null ? saved : registry.Register(request.RequestId, description);
        }
        var source = (description?.Source ?? new InteractionSource()) with
        {
            NodeId = description?.Source.NodeId ?? scopeId,
            NodeName = description?.Source.NodeName ?? nodeName,
            ToolName = GetToolName(request),
            CallId = request.ToolCall?.CallId,
            ProviderRequestId = request.RequestId,
        };
        if (description is not null && registry is not null)
            return registry.Register(request.RequestId, description with { Source = source });
        return description is not null
            ? new UserInputInteraction
            {
                InteractionId = InteractionIdentity.ForProviderRequest(source.NodeId!, request.RequestId),
                Source = source,
                Prompt = description.Prompt,
                InputKind = description.InputKind,
                Payload = description.Payload,
                Arguments = description.Arguments,
            }
            : new ToolApprovalInteraction
            {
                InteractionId = InteractionIdentity.ForProviderRequest(source.NodeId!, request.RequestId),
                Source = source,
                Prompt = $"Allow tool '{source.ToolName}' to run?",
                Arguments = GetArguments(request),
            };
    }

    /// <summary>
    /// 根据人工决策创建一次性或持久范围的 MAF approval 响应。
    /// </summary>
    public static AIContent CreateResponse(ToolApprovalRequestContent request, InteractionResponse response)
    {
        // The approval envelope is a resume mechanism for input tools. A cancelled input
        // must still reach that tool's cancellation branch, without invoking its inner action.
        if (response is UserInputResponse)
            return request.CreateResponse(approved: true);
        if (response is not ToolApprovalDecision decision)
            throw new AgwException(ErrorCodes.InvalidParam, "A tool interaction response is required.");
        var approval = request.CreateResponse(approved: decision.Approved);
        if (decision.Approved && decision.Scope != ApprovalScope.Once)
        {
            approval.AdditionalProperties ??= [];
            approval.AdditionalProperties[GrantScopeProperty] = decision.Scope.ToString();
        }
        return approval;
    }

    public static ToolApprovalResponseContent CreateWorkflowResponse(
        ToolApprovalRequestContent request,
        InteractionResponse response
    ) => (ToolApprovalResponseContent)CreateResponse(request, response);

    /// <summary>
    /// 从 approval 内容解析原始 Tool 名称。
    /// </summary>
    internal static string GetToolName(ToolApprovalRequestContent request) =>
        request.ToolCall switch
        {
            FunctionCallContent functionCall => functionCall.Name,
            _ => request.ToolCall?.CallId ?? "unknown",
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
}
