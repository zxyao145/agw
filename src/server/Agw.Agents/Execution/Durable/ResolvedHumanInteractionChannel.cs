using Agw.Shared.Contracts.Agents;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 将 PostgreSQL 中的人工回答预注入重建后的 HumanInteraction Tool，避免再次等待进程内用户输入。
/// </summary>
internal sealed class ResolvedHumanInteractionChannel : IHumanInteractionChannel
{
    private readonly IReadOnlyList<DurableResolvedInteraction> _interactions;

    /// <summary>
    /// 创建包含本次恢复分段全部已解析回答的预回答 channel。
    /// </summary>
    public ResolvedHumanInteractionChannel(
        IReadOnlyList<DurableResolvedInteraction> interactions)
    {
        _interactions = interactions;
    }

    /// <summary>
    /// 按 callId 或 toolName 找到持久回答，并转换为当前 Tool 请求使用的 response。
    /// </summary>
    public ValueTask<HumanInteractionResponse> RequestAsync(
        HumanInteractionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _interactions.FirstOrDefault(item =>
            (!string.IsNullOrWhiteSpace(request.CallId)
             && string.Equals(item.Request.CallId, request.CallId, StringComparison.Ordinal))
            || (string.IsNullOrWhiteSpace(request.CallId)
                && string.Equals(item.Request.ToolName, request.ToolName, StringComparison.Ordinal)));
        if (resolved == null && _interactions.Count == 1)
        {
            resolved = _interactions[0];
        }
        if (resolved == null)
        {
            throw new AgwException(
                ErrorCodes.DurableExecutionConflict,
                $"No persisted response matches human interaction Tool call '{request.CallId}'.");
        }

        return ValueTask.FromResult(new HumanInteractionResponse(
            request.RequestId,
            Cancelled: !resolved.Response.Approved,
            resolved.Response.ResponseData));
    }
}
