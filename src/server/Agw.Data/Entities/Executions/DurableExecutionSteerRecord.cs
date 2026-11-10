using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Executions;

/// <summary>
/// 一条 Steer 输入的状态：Pending 等待送达，Delivered 已进入模型输入，Returned 已交还客户端。
/// The state of one Steer input: Pending waits for delivery, Delivered reached the model input, Returned went back to the client.
/// </summary>
public enum DurableExecutionSteerStatus
{
    Pending = 0,
    Delivered = 1,
    Returned = 2,
}

/// <summary>
/// Distributed execution 中用户在 Turn 运行期间提交的一条输入。属于 durable_execution 中的一个 Turn，所属用户与该 Turn 相同。
/// One input a user submitted while a turn runs in Distributed execution. It belongs to one turn of durable_execution and shares that turn's owner.
/// </summary>
[Table("execution_steer")]
[EntityTypeConfiguration(typeof(DurableExecutionSteerRecordConfiguration))]
public sealed class DurableExecutionSteerRecord : BaseEntity
{
    /// <summary>
    /// 客户端生成的 steerId，同时是送达后用户消息的 messageId。
    /// The client-generated steerId, also the messageId of the user message after delivery.
    /// </summary>
    public Guid Id { get; set; }

    public Guid TurnId { get; set; }

    public DurableExecutionSteerStatus Status { get; set; }

    /// <summary>
    /// 序列化后的用户输入；其中是用户数据，必须加密保存。
    /// The serialized user input; it is user data and must be stored encrypted.
    /// </summary>
    [Encrypted]
    public string InputJson { get; set; } = string.Empty;

    /// <summary>
    /// 送达时的 Step 序号。
    /// The Step index at delivery.
    /// </summary>
    public int? StepIndex { get; set; }

    /// <summary>
    /// 交还的原因，例如 turn-completed。
    /// Why the input went back, such as turn-completed.
    /// </summary>
    public string? ReturnReason { get; set; }
}
