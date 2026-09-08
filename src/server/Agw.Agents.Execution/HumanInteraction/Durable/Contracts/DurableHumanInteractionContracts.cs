using System.Text.Json;

namespace Agw.Agents.Execution.HumanInteraction.Durable.Contracts;

/// <summary>
/// 通过 PostgreSQL 状态机持久提交的人工回答。
/// </summary>
internal sealed record DurableHumanResponseEnvelope
{
    /// <summary>
    /// 获取回答所属的业务执行标识。
    /// </summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>
    /// 获取回答对应的人工请求标识。
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// 获取用户是否批准该请求。
    /// </summary>
    public required bool Approved { get; init; }

    /// <summary>
    /// 获取用户提交的可选文本回答。
    /// </summary>
    public string? ResponseText { get; init; }

    /// <summary>
    /// 获取 Tool approval 的生效范围。
    /// </summary>
    public string ApprovalScope { get; init; } = "once";

    /// <summary>
    /// 获取结构化人工回答数据。
    /// </summary>
    public JsonElement? ResponseData { get; init; }
}

/// <summary>
/// 可安全重建交互卡片和 Tool 调用的最小快照。questions payload 不包含模型提供的 answers。
/// </summary>
internal sealed record DurableHumanInteractionSnapshot
{
    /// <summary>
    /// 获取人工请求的稳定标识。
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// 获取交互种类，例如 interaction 或 tool-approval。
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// 获取产生请求的 Agentflow 节点标识。
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// 获取产生请求的可选节点名称。
    /// </summary>
    public string? NodeName { get; init; }

    /// <summary>
    /// 获取等待恢复的 Tool 名称。
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// 获取等待恢复的 Tool 调用标识。
    /// </summary>
    public string? CallId { get; init; }

    /// <summary>
    /// 获取展示给用户的提示文本。
    /// </summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// 获取客户端重建交互界面所需的安全载荷。
    /// </summary>
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// 获取恢复 Tool 调用所需的原始参数副本。
    /// </summary>
    public JsonElement? Arguments { get; init; }
}

/// <summary>
/// 将 pending 请求与对应人工回答绑定，供下一 durable segment 恢复。
/// </summary>
/// <param name="Request">上一分段持久化的人工请求。</param>
/// <param name="Response">PostgreSQL 中持久化的人工回答。</param>
internal sealed record DurableResolvedInteraction(
    DurableHumanInteractionSnapshot Request,
    DurableHumanResponseEnvelope Response
);

/// <summary>
/// 从 SignalR HumanResponseCommand 映射得到的 durable 回答请求。
/// </summary>
/// <param name="ExecutionId">回答所属的业务执行标识。</param>
/// <param name="RequestId">回答对应的人工请求标识。</param>
/// <param name="Approved">用户是否批准请求。</param>
/// <param name="ResponseText">用户提交的可选文本回答。</param>
/// <param name="ApprovalScope">Tool approval 的生效范围。</param>
/// <param name="ResponseData">用户提交的可选结构化回答。</param>
internal sealed record SubmitDurableHumanResponseRequest(
    Guid ExecutionId,
    string RequestId,
    bool Approved,
    string? ResponseText = null,
    string ApprovalScope = "once",
    JsonElement? ResponseData = null
);
