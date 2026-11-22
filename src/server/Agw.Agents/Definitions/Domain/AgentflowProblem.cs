namespace Agw.Agents.Definitions.Domain;

/// <summary>
/// 定义中的一项问题；NodeId 与 EdgeId 至多一个有值。
/// One problem in a definition; at most one of NodeId and EdgeId is set.
/// </summary>
public sealed record AgentflowProblem
{
    public AgentflowProblem(string code, string message, string? nodeId, string? edgeId)
    {
        Code = code;
        Message = message;
        NodeId = nodeId;
        EdgeId = edgeId;
    }

    public string Code { get; init; }

    public string Message { get; init; }

    public string? NodeId { get; init; }

    public string? EdgeId { get; init; }
}
