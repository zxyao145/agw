using System.Text.Json;
using Agw.Shared.Contracts.Coordination;
using Microsoft.Extensions.AI;

namespace Agw.Projects.Contracts.History;

/// <summary>
/// 一个写入者的历史归属。TurnId、AgentId、HistoryScope 与快照的 StepIndex、IsResult 在插入时写入对应列。
/// The history ownership of one writer. TurnId, AgentId, HistoryScope and the snapshot's StepIndex and IsResult fill their columns at insert.
/// </summary>
public sealed record ConversationMessageWriteScope
{
    public required Guid ProjectId { get; init; }
    public required string ContextId { get; init; }
    public required int Generation { get; init; }
    public required Guid ProducerId { get; init; }
    public Guid? TurnId { get; init; }
    public Guid? AgentId { get; init; }
    public string? HistoryScope { get; init; }
    public string? NodeName { get; init; }

    /// <summary>Carries the session's binding so snapshot writes keep the existing create-or-reject rules.</summary>
    public bool IsExecutionBound { get; init; }
}

/// <summary>
/// 投影捕获的一条消息。Message 在捕获后不再修改，历史存储用自己的序列化选项写出它。
/// One message captured from a projection. Message is not modified after capture, and the history store writes it with its own serializer options.
/// </summary>
public sealed record ConversationMessageSnapshot
{
    public required Guid MessageId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required ChatMessage Message { get; init; }
    public string? Author { get; init; }
    public int? StepIndex { get; init; }
    public bool IsResult { get; init; }
    public required Dictionary<string, JsonElement> Metadata { get; init; }
}

/// <summary>An execution-local projection. Acknowledge only the exact captured objects after a successful write.</summary>
public interface IConversationMessageSource
{
    IReadOnlyList<Guid> GetPendingMessageIds();
    IReadOnlyList<ConversationMessageSnapshot> CapturePending();
    void Acknowledge(IReadOnlyList<ConversationMessageSnapshot> snapshots);
}

/// <summary>
/// 一次执行读写历史的对话范围。
/// The conversation that one execution reads and writes history for.
/// </summary>
public sealed record ConversationHistoryScope
{
    public required Guid ProjectId { get; init; }
    public required string ContextId { get; init; }
    public required int Generation { get; init; }

    /// <summary>
    /// 为真时对话必须已经存在，缺失即拒绝；为假时首次写入创建对话。
    /// When true the conversation must already exist and a missing one is rejected; when false the first write creates it.
    /// </summary>
    public required bool IsExecutionBound { get; init; }
}

/// <summary>
/// 一条已保存或待提交的历史记录；Payload 是序列化的消息。
/// One saved or pending history record; Payload is the serialized message.
/// </summary>
public sealed record ConversationHistoryEntry(
    Guid Id,
    long? Sequence,
    string Payload,
    IReadOnlyDictionary<string, JsonElement>? Metadata,
    DateTimeOffset CreateTime
);

/// <summary>
/// 一次执行的历史缓冲：按写入模式批量提交，读取时合并尚未提交的内容。
/// The history buffer of one execution: commits in batches by write mode, and reads merge content not yet committed.
/// </summary>
public interface IConversationHistoryBuffer
{
    ConversationHistoryScope Scope { get; }

    /// <summary>
    /// 最近一次提交后对话中的最大序号；尚未提交时为 -1。
    /// The largest conversation sequence after the latest commit; -1 before any commit.
    /// </summary>
    long CommittedSequence { get; }

    Task ScheduleAsync(
        ConversationMessageWriteScope scope,
        IConversationMessageSource source,
        long changedBytes,
        CancellationToken cancellationToken
    );

    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 提交全部待写内容并阻止新的写入，直到返回的租约释放。
    /// Commits every pending write and blocks new writes until the returned lease is released.
    /// </summary>
    Task<IAsyncDisposable> EnterBarrierAsync(CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(Exception? executionFailure);
}

/// <summary>
/// Projects 拥有历史表：读取、按消息身份更新或插入、序号、锁、所属与 Generation 校验。
/// Projects owns the history table: reads, upserts by message identity, sequencing, locking, ownership and generation checks.
/// </summary>
public interface IConversationHistoryStore
{
    /// <summary>
    /// 建立一次执行的缓冲；给出写入入口时，每次提交都在它的租约检查事务中完成。
    /// Starts the buffer of one execution; when a write guard is given, every commit runs inside its lease-checked transaction.
    /// </summary>
    IConversationHistoryBuffer BeginBuffer(
        ConversationHistoryScope scope,
        CancellationToken ownershipLost = default,
        IExecutionWriteGuard? writeGuard = null
    );

    /// <summary>
    /// 按对话顺序读取指定历史作用域的记录；给出缓冲时合并其中尚未提交的内容。返回的条目只包含模型历史需要的列，Metadata 为空。
    /// Reads the records of one history scope in conversation order, merging uncommitted buffer content when a buffer is given. Entries carry only the columns model history needs, with Metadata left null.
    /// </summary>
    Task<IReadOnlyList<ConversationHistoryEntry>> ReadAsync(
        ConversationHistoryScope scope,
        string? historyScope,
        IConversationHistoryBuffer? buffer,
        CancellationToken cancellationToken
    );

    Task UpsertAsync(
        ConversationMessageWriteScope scope,
        IReadOnlyList<ConversationMessageSnapshot> snapshots,
        CancellationToken cancellationToken
    );
}
