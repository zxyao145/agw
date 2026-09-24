using System.Security.Cryptography;
using System.Text;

namespace Agw.Agents.Execution.HumanInteraction.Application;

/// <summary>
/// 一个人工交互请求的完整身份：所属 Turn、节点、activation、Step，以及 SDK 请求与工具调用身份。
/// The complete identity of one human interaction request: its turn, node, activation, Step, and SDK request and tool-call identities.
/// </summary>
internal sealed record InteractionIdentity
{
    public const string StandaloneNodeId = "standalone";

    public required string InteractionId { get; init; }

    public required Guid TurnId { get; init; }

    public required string NodeId { get; init; }

    public required int ActivationIndex { get; init; }

    public required int StepIndex { get; init; }

    public required string ProviderRequestId { get; init; }

    public string? CallId { get; init; }

    public static InteractionIdentity Create(
        Guid turnId,
        string nodeId,
        int activationIndex,
        int stepIndex,
        string providerRequestId,
        string? callId
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRequestId);
        return new InteractionIdentity
        {
            InteractionId = ForProviderRequest(nodeId, providerRequestId),
            TurnId = turnId,
            NodeId = nodeId,
            ActivationIndex = activationIndex,
            StepIndex = stepIndex,
            ProviderRequestId = providerRequestId,
            CallId = callId,
        };
    }

    /// <summary>
    /// 由节点与 SDK 请求身份生成稳定的交互 ID，重发与跨 Segment 恢复时保持不变。
    /// Derives the stable interaction ID from the node and SDK request identity; it survives resends and segment recovery.
    /// </summary>
    public static string ForProviderRequest(string nodeId, string providerRequestId) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{nodeId.Length}:{nodeId}{providerRequestId}"))
        )[..32];

    /// <summary>
    /// 由节点与批次内全部 SDK 请求身份生成稳定的批次 ID；Agent 管线与执行器各自计算得到同一个值。
    /// Derives the stable batch ID from the node and every SDK request identity in the batch; the Agent pipeline and the executor compute the same value independently.
    /// </summary>
    public static string ForBatch(string nodeId, IEnumerable<string> providerRequestIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var builder = new StringBuilder().Append(nodeId.Length).Append(':').Append(nodeId);
        foreach (var requestId in providerRequestIds.Order(StringComparer.Ordinal))
            builder.Append(requestId.Length).Append(':').Append(requestId);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32];
    }
}
