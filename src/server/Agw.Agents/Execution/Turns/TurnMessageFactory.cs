using Agw.Shared.AgwMsgVm;
using Agw.Shared.Extensions;

using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Turns;

/// <summary>
/// 创建客户端可识别的 Turn 生命周期控制消息。
/// </summary>
internal static class TurnMessageFactory
{
    /// <summary>
    /// 创建 Turn 已启动消息，并在 durable 模式下携带稳定的 executionId。
    /// </summary>
    public static AgwMessage CreateStarted(Guid? executionId = null) =>
        CreateState("turn-start", status: null, executionId);

    /// <summary>
    /// 创建 Turn 已结束消息，并在 durable 模式下携带最终状态和 executionId。
    /// </summary>
    public static AgwMessage CreateFinished(
        string status = "completed",
        Guid? executionId = null) =>
        CreateState("turn-finished", status, executionId);

    /// <summary>
    /// 按统一协议构造 Turn 状态消息，避免启动与结束消息的字段发生漂移。
    /// </summary>
    private static AgwMessage CreateState(
        string type,
        string? status,
        Guid? executionId)
    {
        var properties = new AdditionalPropertiesDictionary
        {
            ["type"] = type
        };
        if (status != null)
        {
            properties["status"] = status;
        }
        if (executionId.HasValue)
        {
            properties["executionId"] = executionId.Value.ToString("D");
        }

        return new AgwMessage(
            Guid.CreateVersion7().Normalize(),
            Constants.DefaultAgentAuthor,
            AiRole.System,
            [new AgwTextContent { Content = "" }],
            properties);
    }
}
